// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Tests.Protocol;

/// <summary>
/// Frames captured from a real OPPO Enco X4 (product id 0x06EC10, firmware 116/116/100) with
/// EncoDeck.Probe on 2026-10-20. The source-device list event (0x0204/06) is deliberately not
/// included because it contains other devices' addresses and names.
/// </summary>
public class EncoX4CaptureTests
{
    private static OppoFrame Decode(string hex)
    {
        var frames = new OppoFrameDecoder().Push(Convert.FromHexString(hex.Replace(" ", string.Empty)));
        return Assert.Single(frames);
    }

    [Fact]
    public void RawStreamWithStickyFrames_DecodesAll()
    {
        // Two reads as delivered by the RFCOMM socket (time request + product id response).
        var decoder = new OppoFrameDecoder();
        var frames = decoder.Push(Convert.FromHexString("AA0700000005" + "1C0000"));
        frames = [.. frames, .. decoder.Push(Convert.FromHexString("AA0B0000038102040000" + "10EC06"))];
        Assert.Equal(2, frames.Count);
        Assert.Equal(OppoCommand.TimeRequest, frames[0].Command);
        Assert.True(OppoParsers.TryParseProductId(frames[1], out uint pid));
        Assert.Equal(0x06EC10u, pid);
    }

    [Fact]
    public void Battery_InCaseCharging()
    {
        var frame = Decode("AA 0F 00 00 06 81 F0 08 00 00 03 01 C6 02 C6 03 46");
        Assert.Equal(0xF0, frame.Sequence); // echoes the request sequence
        Assert.True(OppoParsers.TryParseBattery(frame, out var entries));
        Assert.Equal(
            [new OppoBatteryEntry(1, 70, true), new OppoBatteryEntry(2, 70, true), new OppoBatteryEntry(3, 70, false)],
            entries);
    }

    [Fact]
    public void BatteryEvent_BudsOutOfCase_HasNoCaseEntry()
    {
        var frame = Decode("AA 0D 00 00 04 02 24 06 00 01 02 01 5A 02 5A");
        Assert.True(OppoParsers.TryParseBattery(frame, out var entries));
        Assert.Equal([new OppoBatteryEntry(1, 90, false), new OppoBatteryEntry(2, 90, false)], entries);
    }

    [Fact]
    public void NoiseControl_Response_And_Event()
    {
        var response = Decode("AA 0C 00 00 0C 81 04 05 00 00 01 01 08 00");
        Assert.True(OppoParsers.TryParseNoiseReport(response, out var r));
        Assert.Equal(new OppoNoiseReport(1, 1, 0x0008), r);

        var evt = Decode("AA 0C 00 00 04 02 25 05 00 03 01 01 00 20");
        Assert.True(OppoParsers.TryParseNoiseReport(evt, out var e));
        Assert.Equal(new OppoNoiseReport(1, 1, 0x2000), e);

        var smart = Decode("AA 0C 00 00 04 02 26 05 00 03 04 01 40 00");
        Assert.True(OppoParsers.TryParseNoiseReport(smart, out var s));
        Assert.Equal(4, s.Subtype);
    }

    [Fact]
    public void BatteryEvent_IsNotMistakenForNoiseControl()
    {
        // Regression for a bug in earlier community code: "01 01 .." in a battery event looked like an ANC marker.
        var frame = Decode("AA 0B 00 00 04 02 01 04 00 01 01 02 50");
        Assert.False(OppoParsers.TryParseNoiseReport(frame, out _));
        Assert.True(OppoParsers.TryParseBattery(frame, out var entries));
        Assert.Equal([new OppoBatteryEntry(2, 80, false)], entries);
    }

    [Fact]
    public void WearEvent_Flags()
    {
        var frame = Decode("AA 0F 00 00 04 02 27 08 00 02 03 01 05 02 07 03 04");
        Assert.True(OppoParsers.TryParseWearState(frame, out var entries));
        Assert.True(entries[0].IsOutOfCase);
        Assert.False(entries[0].IsInEar);
        Assert.True(entries[1].IsInEar);
        Assert.False(entries[2].IsOutOfCase);
        Assert.True(entries[2].IsLidOpen);
    }

    [Fact]
    public void FeatureStatus()
    {
        var frame = Decode("AA 1B 00 00 0D 81 00 14 00 00 09 05 01 04 01 0B 01 11 01 13 01 18 00 06 01 27 01 28 00");
        Assert.True(OppoParsers.TryParseFeatureStatus(frame, out var values));
        Assert.Equal(9, values.Count);
        Assert.Contains(new OppoFeatureValue(0x04, 1), values);
        Assert.Contains(new OppoFeatureValue(0x28, 0), values);
    }

    [Fact]
    public void FirmwareVersions()
    {
        var frame = Decode("AA 20 00 00 05 81 07 19 00 00 03 31 2C 32 2C 31 31 36 2C 32 2C 32 2C 31 31 36 2C 33 2C 32 2C 31 30 30");
        Assert.True(OppoParsers.TryParseVersions(frame, out var versions));
        Assert.Equal([new OppoVersionEntry(1, 2, "116"), new OppoVersionEntry(2, 2, "116"), new OppoVersionEntry(3, 2, "100")], versions);
    }

    [Fact]
    public void EqualizerList_CustomPreset()
    {
        var frame = Decode("AA 37 00 00 22 81 06 30 00 00 01 00 FA 06 04 0A E8 87 AA E5 AE 9A E4 B9 89 31 0A 1F 00 00 3E 00 00 7D 00 00 FA 00 00 F4 01 00 E8 03 00 D0 07 00 A0 0F 00 40 1F 00 80 3E 00");
        Assert.True(OppoParsers.TryParseEqualizerList(frame, out var entries));
        var entry = Assert.Single(entries);
        Assert.False(entry.IsSelected);
        Assert.Equal(-6, entry.MinGain);
        Assert.Equal(6, entry.MaxGain);
        Assert.Equal(4, entry.Id);
        Assert.Equal("自定义1", entry.Name);
        Assert.Equal(new ushort[] { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 }, entry.Bands.Select(b => b.FrequencyHz));
        Assert.All(entry.Bands, b => Assert.Equal(0, b.Gain));
    }

    [Fact]
    public void EqualizerChangedPush()
    {
        var frame = Decode("AA 08 00 00 04 05 30 01 00 07");
        Assert.True(OppoParsers.TryParseEqualizerChanged(frame, out byte preset));
        Assert.Equal(7, preset);
    }

    [Fact]
    public void EventCodes_AndSubscribeAck()
    {
        var codes = Decode("AA 12 00 00 00 82 03 0B 00 00 09 01 02 03 04 08 0B F1 F2 F3");
        Assert.True(OppoParsers.TryParseEventCodes(codes, out var list));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x08, 0x0B, 0xF1, 0xF2, 0xF3 }, list);
    }

    [Fact]
    public void NoiseControlSetAck()
    {
        var ack = Decode("AA 08 00 00 04 84 F0 01 00 00");
        Assert.True(OppoParsers.TryParseAck(ack, OppoCommand.SetNoiseControl, out byte status));
        Assert.Equal(0, status);
    }

    [Fact]
    public void SourceDeviceList_ParsesSyntheticEntry()
    {
        // Synthetic addresses/names (the real capture is personal data).
        byte[] payload =
        [
            0x06, 0x02,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x02, 0x17, 0x04, (byte)'T', (byte)'E', (byte)'S', (byte)'T',
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x05, 0x00, 0x20, 0x02, (byte)'W', (byte)'X',
        ];
        var frame = new OppoFrame(OppoCommand.DeviceEvent, 0x22, payload);
        Assert.True(OppoParsers.TryParseSourceDevices(frame, out var devices));
        Assert.Equal(2, devices.Count);
        Assert.True(devices[0].IsConnected);
        Assert.True(devices[0].IsCurrentHost);
        Assert.Equal("TEST", devices[0].Name);
        Assert.False(devices[1].IsConnected);
        Assert.True(OppoRedaction.IsSensitive(frame));
        Assert.DoesNotContain("54455354", OppoRedaction.FrameHex(frame)); // "TEST" must not appear
    }
}
