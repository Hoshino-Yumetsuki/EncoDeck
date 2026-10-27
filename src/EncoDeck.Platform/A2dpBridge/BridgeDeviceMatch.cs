// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;

namespace EncoDeck.Platform.A2dpBridge;

/// <summary>Which of EncoDeck's devices the bridge is streaming to: the active profile's address against the Windows pairing's.</summary>
public static class BridgeDeviceMatch
{
    public static BridgeAudioLink? LinkFor(DeviceIdentity? device, BridgeStatus status)
    {
        if (device is not { Transport: DeviceTransport.Bluetooth } || !status.IsConnected) return null;
        var snapshot = status.Snapshot!;
        if (snapshot.ActiveStreamProfile is not { } profile || ParseAddress(profile.DeviceAddress) is not { } address || address != device.Address.Value) return null;
        var stream = snapshot.Stream;
        var info = stream.State == BridgeStreamState.Streaming ? stream.Info : null;
        return new BridgeAudioLink(stream.State == BridgeStreamState.Streaming, info?.Codec is { Length: > 0 } codec ? codec : null,
            info?.BitrateKbps ?? 0, info?.SampleRate ?? 0, profile.Name, CanCarryControl: status.Rfcomm && !profile.LeAudio);
    }

    /// <summary>"AA:BB:CC:DD:EE:FF" (also with '-' or no separators) as the 48-bit address.</summary>
    public static ulong? ParseAddress(string text)
    {
        string hex = new(text.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 && text.Length <= 17 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value) ? value : null;
    }
}
