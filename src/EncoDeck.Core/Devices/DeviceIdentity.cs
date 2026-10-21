// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Privacy;

namespace EncoDeck.Core.Devices;

/// <summary>Stable identification of one physical device known to Windows.</summary>
/// <param name="DeviceId">Windows device interface/association id (opaque; contains the address — do not log it).</param>
/// <param name="Name">Friendly name reported by Windows, e.g. "OPPO Enco X4".</param>
/// <param name="Address">Bluetooth address; formats redacted by default.</param>
public sealed record DeviceIdentity(string DeviceId, string Name, BluetoothAddress Address)
{
    public override string ToString() => $"{Name} ({Address})";
}
