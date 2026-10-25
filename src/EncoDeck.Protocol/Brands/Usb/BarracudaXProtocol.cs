// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Brands.Usb;

/// <summary>Barracuda X receivers. The replacement 0550 uses the official YSTech HID envelope.</summary>
public static class BarracudaXProtocol
{
    public static bool IsReceiver(ushort vendor, ushort product) => vendor == 0x1532 && product is 0x0552 or 0x0550;
    public static bool IsCollection(ushort vendor, ushort product, ushort page, ushort usage) =>
        IsReceiver(vendor, product) && page == 0xFF00 && usage == 1;

    public static byte[] Handshake()
    {
        var report = new byte[64]; report[0] = 1; report[1] = 0x40; return report;
    }

    // AWToolLIB2 HID.ystech_write64: CRC-16/CCITT with initial value 0, over the
    // entire padded report with the CRC field zeroed. The status reply is obtained
    // separately through GetInputReport, not through the interrupt input event.
    public static byte[] ReplacementHandshake() => ReplacementReport(0x84, [1]);

    public static byte[] ReplacementQuery(byte sequence, byte command, bool remote = false)
    {
        byte[] original = Query(sequence, command, remote);
        return ReplacementReport(0x80, original.AsSpan(3, original[2]));
    }

    private static byte[] ReplacementReport(byte command, ReadOnlySpan<byte> payload)
    {
        var report = new byte[64];
        report[0] = 2; report[1] = command; report[2] = (byte)payload.Length;
        payload.CopyTo(report.AsSpan(5));
        ushort crc = 0;
        foreach (byte value in report)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        report[3] = (byte)crc; report[4] = (byte)(crc >> 8);
        return report;
    }

    public static byte[] Query(byte sequence, byte command, bool remote = false)
    {
        if (sequence is 0 or > 127) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (command is not (0xE0 or 0xE3 or 0xE1 or 0x21 or 0x2A)) throw new ArgumentOutOfRangeException(nameof(command));
        bool telemetry = command is 0x21 or 0x2A;
        byte[] body = telemetry ? [command, 0, 0] : command == 0xE1 ? [command, remote ? (byte)1 : (byte)0] : [command];
        var report = new byte[64];
        report[0] = 1; report[1] = 0x80; report[2] = (byte)(5 + body.Length);
        report[3] = 0x50; report[4] = 0x41; report[5] = telemetry ? (byte)8 : (byte)0x0E;
        report[6] = sequence; report[7] = (byte)body.Length; body.CopyTo(report, 8);
        return report;
    }

    public static bool IsAck(ReadOnlySpan<byte> frame, byte sequence) =>
        frame.Length == 13 && frame[0] == 0x50 && frame[1] == 0x49 && frame[2] == 1 &&
        frame[8] == 3 && frame[9] == 0 && frame[10] == 0x0E && frame[11] == (sequence | 0x80) && frame[12] == 0;

    public static bool? Link(ReadOnlySpan<byte> frame) =>
        frame.Length == 12 && frame[0] == 0x50 && frame[1] == 0x49 && frame[2] == 0x0E &&
        frame[8] == 2 && frame[9] == 0 && frame[10] == 0xE3 && frame[11] <= 1 ? frame[11] == 1 : null;

    public static int? Route(ReadOnlySpan<byte> frame) =>
        frame.Length == 12 && frame[0] == 0x50 && frame[1] == 0x49 && frame[2] == 0x0E &&
        frame[8] == 2 && frame[9] == 0 && frame[10] == 0xE0 && frame[11] <= 1 ? frame[11] : null;

    public static int? Value(ReadOnlySpan<byte> frame, byte command) =>
        command is 0x21 or 0x2A && frame.Length == 14 && frame[0] == 0x50 && frame[1] == 0x49 && frame[2] == 8 &&
        frame[8] == 4 && frame[9] == 0 && frame[10] == command && frame[11] == 1 && frame[12] == 1 &&
        frame[13] <= (command == 0x21 ? 100 : 1) ? frame[13] : null;
}

/// <summary>Reassembles PI messages across HID chunks; padding is never parsed as data.</summary>
public sealed class BarracudaXDecoder
{
    private readonly List<byte> _pending = [];
    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> report, bool replacement = false)
    {
        int header = replacement ? 2 : 3;
        int count = report.Length >= header ? report[header - 1] : 0;
        if (report.Length < header || report[0] != (replacement ? 2 : 1) ||
            (!replacement && report[1] != 0x80) || count > report.Length - header)
        { _pending.Clear(); return []; }
        _pending.AddRange(report.Slice(header, count).ToArray());
        var frames = new List<byte[]>();
        while (_pending.Count >= 10)
        {
            int length = 10 + _pending[8] + (_pending[9] << 8);
            if (_pending[0] != 0x50 || _pending[1] != 0x49 || length > 512)
            { _pending.Clear(); break; }
            if (_pending.Count < length) break;
            frames.Add(_pending.GetRange(0, length).ToArray()); _pending.RemoveRange(0, length);
        }
        return frames;
    }
}
