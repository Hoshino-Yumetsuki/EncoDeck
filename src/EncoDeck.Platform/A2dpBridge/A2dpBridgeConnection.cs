// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace EncoDeck.Platform.A2dpBridge;

/// <summary>The core refused the connection (another protocol version).</summary>
public sealed class BridgeRejectedException(string reason) : IOException(reason);

/// <summary>An error answer of the core: a code the UI can act on ("adapter.driver_missing") and its message.</summary>
public sealed class BridgeRequestException(string code, BridgeMessage message) : IOException($"{code}: {message.Key}")
{
    public string Code { get; } = code;

    public BridgeMessage BridgeMessage { get; } = message;
}

/// <summary>
/// One session on the core's per-user pipe (<c>\\.\pipe\a2dpwb-&lt;SID&gt;</c>): length-prefixed JSON frames,
/// hello → welcome + snapshot, then events with consecutive revisions and request/response pairs. EncoDeck
/// subscribes to nothing high-rate, so the core sends only state changes.
/// </summary>
internal sealed class A2dpBridgeConnection : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    private const int MaxFrame = 4 << 20;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly Stream _pipe;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    // Reader-thread state: the last revision applied, and a resynchronisation in flight.
    private long _revision;
    private List<JsonObject>? _resync;
    private long _resyncId;
    // Set before the reader fails the pending requests: a request registered later sees it and fails at once.
    private volatile bool _closed;
    private Task _reader = Task.CompletedTask;

    private A2dpBridgeConnection(Stream pipe, string server, BridgeSnapshot snapshot, long revision)
    {
        _pipe = pipe;
        Server = server;
        Snapshot = snapshot;
        _revision = revision;
    }

    /// <summary>"a2dpwb-core/2.0.1".</summary>
    public string Server { get; }

    /// <summary>Replaced (never mutated) on the reader thread.</summary>
    public BridgeSnapshot Snapshot { get; private set; }

    /// <summary>Raised on the reader thread after every state change.</summary>
    public event Action<BridgeSnapshot>? Changed;

    /// <summary>Completes when the core closes the pipe, says bye, or the connection is disposed.</summary>
    public Task Completion => _reader;

    public static async Task<A2dpBridgeConnection> OpenAsync(string pipeName, string client, TimeSpan connectTimeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectTimeout, ct).ConfigureAwait(false);
            return await HandshakeAsync(pipe, client, ct).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The handshake on an open stream (tests use an in-memory duplex stream).</summary>
    internal static async Task<A2dpBridgeConnection> HandshakeAsync(Stream pipe, string client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        await WriteFrameAsync(pipe, new JsonObject { ["type"] = "hello", ["protocol"] = ProtocolVersion, ["client"] = client }, timeout.Token).ConfigureAwait(false);
        var welcome = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false) ?? throw new IOException("A2DPWB closed the connection.");
        switch (BridgeJson.Str(welcome["type"]))
        {
            case "welcome": break;
            case "reject": throw new BridgeRejectedException(BridgeJson.Str(welcome["reason"]) ?? "rejected");
            default: throw new InvalidDataException("A2DPWB did not answer hello.");
        }
        string server = BridgeJson.Str(welcome["server"]) ?? "a2dpwb-core";
        var first = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false) ?? throw new IOException("A2DPWB closed the connection.");
        if (BridgeJson.Str(first["type"]) != "snapshot" || first["state"] is not JsonObject state) throw new InvalidDataException("A2DPWB sent no snapshot.");
        var connection = new A2dpBridgeConnection(pipe, server, BridgeJson.Snapshot(state), BridgeJson.Long(first["rev"]) ?? 0);
        connection._reader = Task.Run(connection.ReadLoopAsync);
        return connection;
    }

    /// <summary>Sends a request and waits for its response; the core's error answers throw <see cref="BridgeRequestException"/>.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            if (_closed) throw new IOException("A2DPWB closed the connection.");
            var request = new JsonObject { ["type"] = "request", ["id"] = id, ["method"] = method };
            if (parameters is not null) request["params"] = parameters;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            await _write.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await WriteFrameAsync(_pipe, request, linked.Token).ConfigureAwait(false); }
            finally { _write.Release(); }
            linked.CancelAfter(timeout);
            try { return await answer.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (_stop.IsCancellationRequested || _closed) throw new IOException("A2DPWB closed the connection.");
                throw new TimeoutException($"A2DPWB did not answer {method}.");
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(_pipe, _stop.Token).ConfigureAwait(false);
                if (frame is null) break;
                switch (BridgeJson.Str(frame["type"]))
                {
                    case "response":
                        long id = BridgeJson.Long(frame["id"]) ?? -1;
                        if (id == _resyncId)
                        {
                            _resyncId = 0;
                            if (frame["result"] is JsonObject fresh) ApplyResync(fresh);
                            else _resync = null;
                        }
                        else if (_pending.TryGetValue(id, out var answer))
                        {
                            if (frame["error"] is JsonObject error)
                                answer.TrySetException(new BridgeRequestException(BridgeJson.Str(error["code"]) ?? "error",
                                    BridgeJson.Message(error["message"]) ?? BridgeMessage.Empty));
                            else answer.TrySetResult(frame["result"]?.DeepClone());
                        }
                        break;
                    case "snapshot" when frame["state"] is JsonObject state:
                        // The core's own snapshot (it noticed this reader fell behind) carries its true revision.
                        _resync = null;
                        _resyncId = 0;
                        _revision = BridgeJson.Long(frame["rev"]) ?? _revision;
                        Publish(BridgeJson.Snapshot(state));
                        break;
                    case "event" when frame["event"] is JsonObject @event:
                        string? kind = BridgeJson.Str(@event["kind"]);
                        if (kind is "link" or "log" or "receiver") break; // Not state, no revision; never subscribed.
                        if (kind == "bye") return;
                        long rev = BridgeJson.Long(frame["rev"]) ?? 0;
                        // Subscribed before the snapshot was taken: events up to its revision are already in it.
                        if (rev <= _revision) break;
                        bool gap = rev > _revision + 1;
                        _revision = rev;
                        _resync?.Add(@event);
                        if (BridgeJson.Apply(Snapshot, @event, out _) is { } next) Publish(next);
                        if (gap && _resync is null) StartResync();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            failure = ex;
        }
        finally
        {
            _closed = true;
            var closed = failure ?? new IOException("A2DPWB closed the connection.");
            foreach (var pending in _pending.Values) pending.TrySetException(closed);
        }
    }

    /// <summary>A lost event: ask for the whole state and replay the events that arrive meanwhile on top of it, as A2DPWB's window does.</summary>
    private void StartResync()
    {
        _resync = [];
        long id = Interlocked.Increment(ref _nextId);
        _resyncId = id;
        _ = SendAsync(new JsonObject { ["type"] = "request", ["id"] = id, ["method"] = "state.snapshot" });
    }

    private void ApplyResync(JsonObject state)
    {
        var snapshot = BridgeJson.Snapshot(state);
        foreach (var @event in _resync ?? []) snapshot = BridgeJson.Apply(snapshot, @event, out _) ?? snapshot;
        _resync = null;
        Publish(snapshot);
    }

    private void Publish(BridgeSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }

    private async Task SendAsync(JsonObject message)
    {
        try
        {
            await _write.WaitAsync(_stop.Token).ConfigureAwait(false);
            try { await WriteFrameAsync(_pipe, message, _stop.Token).ConfigureAwait(false); }
            finally { _write.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    internal static async Task WriteFrameAsync(Stream stream, JsonNode message, CancellationToken ct)
    {
        byte[] json = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (json.Length > MaxFrame) throw new InvalidDataException("Frame too large.");
        byte[] frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
        json.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One frame, or null at the end of the stream.</summary>
    internal static async Task<JsonObject?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int got = 0;
        while (got < 4)
        {
            int n = await stream.ReadAsync(header.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (got == 0) return null;
                throw new IOException("A2DPWB frame was cut off.");
            }
            got += n;
        }
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrame) throw new InvalidDataException("A2DPWB frame is too large.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("A2DPWB frame is not an object.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); } catch (Exception) { }
        _stop.Dispose();
    }
}
