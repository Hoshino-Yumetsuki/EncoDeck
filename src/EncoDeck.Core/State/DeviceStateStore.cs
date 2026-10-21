// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

public sealed class DeviceStateChangedEventArgs(DeviceState previous, DeviceState current) : EventArgs
{
    public DeviceState Previous { get; } = previous;
    public DeviceState Current { get; } = current;
}

public interface IDeviceStateStore
{
    DeviceState Current { get; }

    /// <summary>
    /// Raised after every change, on the thread that made it. Handlers must not block;
    /// UI subscribers should marshal to their dispatcher and re-read <see cref="Current"/>.
    /// </summary>
    event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Applies <paramref name="mutate"/> atomically. When <paramref name="expectedDeviceId"/> is not null
    /// and the active device differs, the update is dropped and false is returned — this keeps late
    /// results from a previous device from leaking into the newly selected one.
    /// </summary>
    bool TryUpdate(string? expectedDeviceId, Func<DeviceState, DeviceState> mutate);

    /// <summary>Replaces the whole state, e.g. when switching the active device.</summary>
    void Reset(DeviceState state);
}

public sealed class DeviceStateStore : IDeviceStateStore
{
    private readonly Lock _gate = new();
    private DeviceState _current = DeviceState.Empty;

    public DeviceState Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    public bool TryUpdate(string? expectedDeviceId, Func<DeviceState, DeviceState> mutate)
    {
        DeviceState previous, next;
        lock (_gate)
        {
            previous = _current;
            if (expectedDeviceId is not null && !string.Equals(previous.Device?.DeviceId, expectedDeviceId, StringComparison.Ordinal))
            {
                return false;
            }
            next = mutate(previous);
            if (ReferenceEquals(next, previous) || next == previous with { Revision = next.Revision })
            {
                return true;
            }
            next = next with { Revision = previous.Revision + 1 };
            _current = next;
        }
        StateChanged?.Invoke(this, new DeviceStateChangedEventArgs(previous, next));
        return true;
    }

    public void Reset(DeviceState state)
    {
        DeviceState previous, next;
        lock (_gate)
        {
            previous = _current;
            next = state with { Revision = previous.Revision + 1 };
            _current = next;
        }
        StateChanged?.Invoke(this, new DeviceStateChangedEventArgs(previous, next));
    }
}
