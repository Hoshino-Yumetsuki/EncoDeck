// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Oppo;

/// <summary>
/// Strict parsers for the feature commands, following the official client's layouts
/// (docs/PROTOCOL_FINDINGS.md). Each returns false rather than guessing on malformed input.
/// </summary>
public static class OppoFeatureParsers
{
    private static bool IsOkResponse(OppoFrame frame, ushort request, int minLength) =>
        frame.Command == OppoCommand.ResponseOf(request) && frame.Payload.Length >= minLength && frame.Payload[0] == OppoParsers.StatusOk;

    /// <summary><c>0x812A [st][type]</c>: 0 off, 1 fixed, 2 head tracking.</summary>
    public static bool TryParseSpatialType(OppoFrame frame, out SpatialAudioMode mode)
    {
        mode = SpatialAudioMode.Off;
        if (!IsOkResponse(frame, OppoCommand.QuerySpatialType, 2) || frame.Payload[1] > 2) return false;
        mode = (SpatialAudioMode)frame.Payload[1];
        return true;
    }

    /// <summary><c>0x812B [st][selected][n][types×n]</c>.</summary>
    public static bool TryParseGameSound(OppoFrame frame, out GameSoundState state)
    {
        state = new GameSoundState(0, []);
        var p = frame.Payload;
        if (!IsOkResponse(frame, OppoCommand.QueryGameSound, 3)) return false;
        int count = p[2];
        if (p.Length < 3 + count) return false;
        state = new GameSoundState(p[1], p.AsSpan(3, count).ToArray().Select(b => (int)b).ToArray());
        return true;
    }

    /// <summary><c>0x8130 [st][volume 1..10]</c>.</summary>
    public static bool TryParsePromptVolume(OppoFrame frame, out int volume)
    {
        volume = 0;
        if (!IsOkResponse(frame, OppoCommand.QueryPromptVolume, 2)) return false;
        volume = frame.Payload[1];
        return volume is >= 1 and <= 10;
    }

    /// <summary><c>0x8124 [st][min][max][cur]</c> or push <c>0x0508 [min][max][cur]</c> (signed bytes).</summary>
    public static bool TryParseBassEngine(OppoFrame frame, out BassEngineState state)
    {
        state = new BassEngineState(0, 0, 0);
        var p = frame.Payload;
        int offset;
        if (IsOkResponse(frame, OppoCommand.QueryBassEngine, 4)) offset = 1;
        else if (frame.Command == OppoCommand.BassEngineChangedNotification && p.Length >= 3) offset = 0;
        else return false;
        int min = (sbyte)p[offset], max = (sbyte)p[offset + 1], cur = (sbyte)p[offset + 2];
        if (min > max) return false;
        state = new BassEngineState(min, max, Math.Clamp(cur, min, max));
        return true;
    }

    /// <summary><c>0x8108 [st][n]{devType, button, action, function}×n</c>.</summary>
    public static bool TryParseKeyFunctions(OppoFrame frame, out IReadOnlyList<KeyFunction> functions)
    {
        functions = [];
        var p = frame.Payload;
        if (!IsOkResponse(frame, OppoCommand.QueryTouchControls, 2)) return false;
        int count = p[1];
        if (count == 0 || p.Length < 2 + count * 4) return false;
        var list = new List<KeyFunction>(count);
        for (int i = 0; i < count; i++)
        {
            int o = 2 + i * 4;
            if (!Enum.IsDefined((EarSide)p[o]) || !Enum.IsDefined((GestureAction)p[o + 2])) continue;
            list.Add(new KeyFunction((EarSide)p[o], p[o + 1], (GestureAction)p[o + 2], p[o + 3]));
        }
        functions = list;
        return true;
    }

    /// <summary>Event <c>[04][dev][st][dev][st]</c>: st 1 good, 0 average, 6 poor, anything else failed.</summary>
    public static bool TryParseFitTest(OppoFrame frame, out FitTestResult result)
    {
        result = new FitTestResult(FitQuality.Unknown, FitQuality.Unknown);
        var p = frame.Payload;
        if (frame.Command != OppoCommand.DeviceEvent || p.Length < 5 || p[0] != OppoEventCode.FitTest) return false;
        FitQuality left = FitQuality.Unknown, right = FitQuality.Unknown;
        for (int i = 1; i + 1 < p.Length && i <= 3; i += 2)
        {
            var quality = p[i + 1] switch
            {
                1 => FitQuality.Good,
                0 => FitQuality.Average,
                6 => FitQuality.Poor,
                _ => FitQuality.Failed,
            };
            if (p[i] == 1) left = quality;
            else if (p[i] == 2) right = quality;
        }
        result = new FitTestResult(left, right);
        return true;
    }

    /// <summary><c>0x8132 [st][02][flag][mac6 reversed]</c>; flag 0 = automatic.</summary>
    public static bool TryParsePreferredDevice(OppoFrame frame, out PreferredDeviceState state)
    {
        state = new PreferredDeviceState(null);
        var p = frame.Payload;
        if (!IsOkResponse(frame, OppoCommand.QueryPriorityDevice, 3) || p[1] != 0x02) return false;
        if (p[2] == 0 || p.Length < 9)
        {
            return true;
        }
        state = new PreferredDeviceState(ReverseAddress(p.AsSpan(3, 6)));
        return true;
    }

    /// <summary><c>0x8134 [st][type]</c>.</summary>
    public static bool TryParseHeadMotionType(OppoFrame frame, out int type)
    {
        type = 0;
        if (!IsOkResponse(frame, OppoCommand.QueryHeadMotionType, 2)) return false;
        type = frame.Payload[1];
        return type is 0 or 1;
    }

    /// <summary><c>0x8136 [st][bits]</c>: bit0 switch on, bit1 owner, bit2 reset flag (official parser).</summary>
    public static bool TryParseLeAudioSwitch(OppoFrame frame, out LeAudioSwitchState state)
    {
        state = new LeAudioSwitchState(false, false, false);
        if (!IsOkResponse(frame, OppoCommand.QueryLeBrSwitch, 2)) return false;
        int bits = frame.Payload[1];
        state = new LeAudioSwitchState((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
        return true;
    }

    /// <summary>Custom EQ list from <c>0x8122</c> or the <c>0x0506</c> push (same body without status).</summary>
    public static bool TryParseCustomEqualizers(OppoFrame frame, out IReadOnlyList<CustomEqualizer> presets)
    {
        presets = [];
        OppoFrame normalized = frame;
        if (frame.Command == OppoCommand.CustomEqualizerChangedNotification)
        {
            normalized = new OppoFrame(OppoCommand.ResponseOf(OppoCommand.QueryEqualizerList), frame.Sequence, [OppoParsers.StatusOk, .. frame.Payload]);
        }
        if (!OppoParsers.TryParseEqualizerList(normalized, out var entries)) return false;
        presets = entries.Select(e => new CustomEqualizer(
            e.Id,
            e.Name,
            e.MinGain,
            e.MaxGain,
            e.IsSelected,
            e.Bands.Select(b => new EqualizerBand(b.FrequencyHz, b.Gain)).ToArray())).ToArray();
        return true;
    }

    /// <summary>Source devices from event 06 / <c>0x8112</c>, with addresses converted to display order.</summary>
    public static bool TryParseSourceDevices(OppoFrame frame, out IReadOnlyList<SourceDevice> devices)
    {
        devices = [];
        if (!OppoParsers.TryParseSourceDevices(frame, out var raw)) return false;
        devices = raw
            .Select(d => new SourceDevice(ReverseAddress(d.AddressLittleEndian), d.Name, d.State, d.IsCurrentHost, d.IsAudioActive, d.DeviceType))
            .OrderByDescending(d => d.IsThisDevice)
            .ThenByDescending(d => d.IsConnected)
            .ToArray();
        return true;
    }

    /// <summary>The earbuds send addresses byte-reversed; <c>0x0429</c> takes them in display order.</summary>
    public static byte[] ReverseAddress(ReadOnlySpan<byte> address)
    {
        var copy = address.ToArray();
        Array.Reverse(copy);
        return copy;
    }

    /// <summary>Serializes a custom EQ record for <c>0x0418</c>: <c>[action][min][max][id][nameLen][name][n]{freq u16 LE, gain i8}</c>.</summary>
    public static byte[] EncodeCustomEqualizer(byte action, CustomEqualizer preset)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(preset.Name);
        if (name.Length > 255) throw new ArgumentException("Preset name too long.");
        var bytes = new List<byte>
        {
            action,
            unchecked((byte)(sbyte)preset.MinGain),
            unchecked((byte)(sbyte)preset.MaxGain),
            (byte)preset.Id,
            (byte)name.Length,
        };
        bytes.AddRange(name);
        bytes.Add((byte)preset.Bands.Count);
        foreach (var band in preset.Bands)
        {
            bytes.Add((byte)(band.FrequencyHz & 0xFF));
            bytes.Add((byte)(band.FrequencyHz >> 8));
            bytes.Add(unchecked((byte)(sbyte)Math.Clamp(band.Gain, preset.MinGain, preset.MaxGain)));
        }
        return bytes.ToArray();
    }
}
