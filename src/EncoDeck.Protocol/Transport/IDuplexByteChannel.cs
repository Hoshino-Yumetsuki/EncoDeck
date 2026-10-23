// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Transport;

/// <summary>
/// A connected, ordered byte stream (an RFCOMM socket in production, a fake in tests).
/// Implementations must make <see cref="DisposeAsync"/> abort any pending read.
/// </summary>
public interface IDuplexByteChannel : IAsyncDisposable
{
    /// <summary>Human-readable description for logs (must not contain a full device address).</summary>
    string Description { get; }

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Reads at least one byte, or returns 0 when the remote side closed the channel.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>Opens control channels for a specific device.</summary>
public interface IChannelConnector
{
    Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken);
}

public enum ChannelOpenStatus
{
    Opened,
    /// <summary>None of the requested RFCOMM services exist in the device's SDP record.</summary>
    ServiceNotFound,
    /// <summary>Device is out of range / not connected.</summary>
    DeviceUnreachable,
    /// <summary>Windows denied access (privacy settings, radio off).</summary>
    AccessDenied,
    /// <summary>The service exists but the socket could not be connected.</summary>
    ConnectFailed,
    /// <summary>The selected device's LE control endpoint was not found during bounded discovery.</summary>
    ControlEndpointNotFound,
}

public sealed record ChannelOpenResult(ChannelOpenStatus Status, IDuplexByteChannel? Channel, Guid? ServiceUuid, string? Detail)
{
    public static ChannelOpenResult Failed(ChannelOpenStatus status, string detail) => new(status, null, null, detail);
}
