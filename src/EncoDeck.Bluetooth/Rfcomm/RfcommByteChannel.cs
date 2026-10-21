// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using EncoDeck.Protocol.Transport;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace EncoDeck.Bluetooth.Rfcomm;

/// <summary><see cref="IDuplexByteChannel"/> over a connected RFCOMM <see cref="StreamSocket"/>.</summary>
internal sealed class RfcommByteChannel : IDuplexByteChannel
{
    private readonly StreamSocket _socket;
    private readonly IInputStream _input;
    private readonly IOutputStream _output;
    private int _disposed;

    public RfcommByteChannel(StreamSocket socket, string description)
    {
        _socket = socket;
        _input = socket.InputStream;
        _output = socket.OutputStream;
        Description = description;
    }

    public string Description { get; }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IBuffer buffer = data.ToArray().AsBuffer();
        uint written = await _output.WriteAsync(buffer).AsTask(cancellationToken);
        if (written != buffer.Length) throw new IOException($"Short RFCOMM write ({written}/{buffer.Length}).");
        await _output.FlushAsync().AsTask(cancellationToken);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var buffer = new Windows.Storage.Streams.Buffer((uint)destination.Length);
        IBuffer result;
        try
        {
            result = await _input.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.Partial).AsTask(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        int count = (int)result.Length;
        if (count > 0)
        {
            result.ToArray(0, count).CopyTo(destination.Span);
        }
        return count;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Disposing the socket aborts any pending ReadAsync on it.
            _socket.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
