// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Oppo;

/// <summary>
/// Removes personal data from frames before they are logged or written to a diagnostic report.
/// Known sensitive payloads:
/// <list type="bullet">
/// <item>Device event 0x06 / connected-device list: Bluetooth addresses and names of the user's phones/PCs.</item>
/// <item>Handshake response: carries a 16-byte value of unknown meaning that may identify the unit.</item>
/// </list>
/// </summary>
public static class OppoRedaction
{
    public static bool IsSensitive(OppoFrame frame)
    {
        var p = frame.Payload;
        return frame.Command switch
        {
            OppoCommand.DeviceEvent => p.Length > 0 && p[0] == OppoEventCode.ConnectedDevices,
            _ when frame.Command == OppoCommand.ResponseOf(OppoCommand.Handshake) => p.Length > 1,
            _ when frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryConnectedDevices) => p.Length > 1,
            _ => false,
        };
    }

    /// <summary>Payload as hex with sensitive bytes masked, keeping the leading structure bytes.</summary>
    public static string PayloadHex(OppoFrame frame)
    {
        if (!IsSensitive(frame)) return Convert.ToHexString(frame.Payload);
        int keep = Math.Min(2, frame.Payload.Length);
        return Convert.ToHexString(frame.Payload, 0, keep) + $"<redacted {frame.Payload.Length - keep} bytes>";
    }

    /// <summary>Whole frame as hex with sensitive payload bytes masked.</summary>
    public static string FrameHex(OppoFrame frame)
    {
        if (!IsSensitive(frame)) return Convert.ToHexString(frame.Encode());
        var header = frame.Encode().AsSpan(0, OppoFrame.HeaderLength);
        return Convert.ToHexString(header) + PayloadHex(frame);
    }
}
