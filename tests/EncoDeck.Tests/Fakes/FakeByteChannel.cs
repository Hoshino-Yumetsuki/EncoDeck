// SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Channels;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IDuplexByteChannel"/> for tests only. Records writes and lets a test inject
/// incoming bytes, optionally via an auto-responder that emulates a device.
/// </summary>
internal sealed class FakeByteChannel : IDuplexByteChannel
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly Lock _gate = new();
    private readonly List<byte[]> _written = [];
    private readonly CancellationTokenSource _disposed = new();

    public string Description => "fake";

    /// <summary>Called for every written frame; return frames to "receive" in response (or null).</summary>
    public Func<OppoFrame, IEnumerable<OppoFrame>?>? Responder { get; set; }

    public Exception? FailWritesWith { get; set; }

    public bool IsDisposed => _disposed.IsCancellationRequested;

    public IReadOnlyList<byte[]> Written
    {
        get { lock (_gate) return _written.ToList(); }
    }

    public IReadOnlyList<OppoFrame> WrittenFrames => Written
        .Select(b => OppoFrame.TryParse(b, out var f) ? f : throw new InvalidOperationException("Non-frame write"))
        .ToList();

    public void Receive(byte[] data) => _incoming.Writer.TryWrite(data);

    public void Receive(OppoFrame frame) => Receive(frame.Encode());

    /// <summary>Simulates the device closing the link.</summary>
    public void RemoteClose() => _incoming.Writer.TryComplete();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (FailWritesWith is { } ex) throw ex;
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        byte[] copy = data.ToArray();
        lock (_gate) _written.Add(copy);
        if (Responder is not null && OppoFrame.TryParse(copy, out var frame))
        {
            foreach (var reply in Responder(frame) ?? []) Receive(reply);
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        try
        {
            if (!await _incoming.Reader.WaitToReadAsync(linked.Token)) return 0;
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        if (!_incoming.Reader.TryRead(out var chunk)) return 0;
        if (chunk.Length > buffer.Length)
        {
            // Push the remainder back to emulate partial reads.
            chunk.AsSpan(0, buffer.Length).CopyTo(buffer.Span);
            var rest = chunk[buffer.Length..];
            var others = new List<byte[]> { rest };
            while (_incoming.Reader.TryRead(out var more)) others.Add(more);
            foreach (var o in others) _incoming.Writer.TryWrite(o);
            return buffer.Length;
        }
        chunk.CopyTo(buffer.Span);
        return chunk.Length;
    }

    public ValueTask DisposeAsync()
    {
        _disposed.Cancel();
        return ValueTask.CompletedTask;
    }
}
