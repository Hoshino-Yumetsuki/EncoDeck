// SPDX-License-Identifier: GPL-3.0-only
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace EncoDeck.Platform.Instance;

/// <summary>
/// Ensures one EncoDeck per user session. The first instance owns a named mutex and listens on a
/// named pipe; later instances forward their command line to it and exit.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listener;

    private SingleInstanceGuard(Mutex mutex, string pipeName, bool isPrimary)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    /// <summary>Raised on a thread-pool thread when another instance was started.</summary>
    public event EventHandler<string[]>? ActivationRequested;

    public static SingleInstanceGuard Acquire(string appKey = "EncoDeck")
    {
        string userKey = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string id = $"{appKey}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(userKey)).AsSpan(0, 6))}";
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{id}", out bool createdNew);
        return new SingleInstanceGuard(mutex, id, createdNew);
    }

    public void StartListening()
    {
        if (!IsPrimary || _listener is not null) return;
        _listener = Task.Run(() => ListenAsync(_cts.Token));
    }

    /// <summary>Sends <paramref name="args"/> to the primary instance. Returns false when it could not be reached.</summary>
    public async Task<bool> SignalPrimaryAsync(string[] args, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', args));
            await client.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                string[] args = text.Length == 0 ? [] : text.Split('\n');
                ActivationRequested?.Invoke(this, args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client disconnected mid-message; keep listening.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a different thread than the one that acquired it; the handle close below still frees it.
            }
        }
        _mutex.Dispose();
        _cts.Dispose();
    }
}
