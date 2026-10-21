// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Frozen;

namespace EncoDeck.Protocol.Oppo;

/// <summary>
/// The remote capability bitmap returned by the handshake (<c>0x8100 [status][bitmap…]</c>).
/// Bit i (LSB-first per byte) enables a group of commands. The bit→command table is an
/// interoperability fact documented from the official client; the official client never sends a
/// command whose bit is clear, and neither does EncoDeck.
/// </summary>
public sealed class OppoCapabilityBitmap
{
    // Commands that are allowed regardless of the bitmap.
    private static readonly FrozenSet<ushort> AlwaysAllowed = new ushort[]
    {
        0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x0106, 0x010B, 0x010D, 0x0F00, 0x0F03, 0x0F04,
        // Notification capability query and registration of single events.
        0x0200, 0x0201,
    }.ToFrozenSet();

    private static readonly FrozenDictionary<ushort, int> CommandToBit = BuildTable();

    private readonly byte[] _bits;

    public OppoCapabilityBitmap(ReadOnlySpan<byte> bits)
    {
        _bits = bits.ToArray();
    }

    public int BitCount => _bits.Length * 8;

    /// <summary>The raw bitmap (to remember it between sessions).</summary>
    public byte[] ToArray() => [.. _bits];

    public static bool TryParse(OppoFrame handshakeResponse, out OppoCapabilityBitmap? bitmap)
    {
        bitmap = null;
        if (handshakeResponse.Command != OppoCommand.ResponseOf(OppoCommand.Handshake)) return false;
        var p = handshakeResponse.Payload;
        if (p.Length < 2 || p[0] != OppoParsers.StatusOk) return false;
        bitmap = new OppoCapabilityBitmap(p.AsSpan(1));
        return true;
    }

    public bool IsBitSet(int bit) => bit >= 0 && bit / 8 < _bits.Length && (_bits[bit / 8] & (1 << (bit % 8))) != 0;

    /// <summary>Whether <paramref name="command"/> (request id) may be sent to this device.</summary>
    public bool Allows(ushort command)
    {
        command = (ushort)(command & 0x7FFF);
        if (AlwaysAllowed.Contains(command)) return true;
        // Commands without a capability row are not gated by the official client either.
        return !CommandToBit.TryGetValue(command, out int bit) || IsBitSet(bit);
    }

    public IEnumerable<int> SetBits()
    {
        for (int i = 0; i < BitCount; i++)
        {
            if (IsBitSet(i)) yield return i;
        }
    }

    public override string ToString() => Convert.ToHexString(_bits);

    private static FrozenDictionary<ushort, int> BuildTable()
    {
        // Row index = bit. Source: official client capability table (see docs/PROTOCOL_FINDINGS.md).
        var rows = new Dictionary<int, ushort[]>
        {
            [0] = [0x0105],
            [1] = [0x0106],
            [2] = [0x0107],
            [3] = [0x0108, 0x0401, 0x0416],
            [4] = [0x0109],
            [5] = [0x0400],
            [6] = [0x0402],
            [7] = [0x0403],
            [8] = [0x010C, 0x0404],
            [9] = [0x0405],
            [10] = [0x0406, 0x010F],
            [11] = [0x0407],
            [13] = [0x0408],
            [14] = [0x0409],
            [17] = [0x0114],
            [19] = [0x040E, 0x040D, 0x0115, 0x0116],
            [20] = [0x040F],
            [21] = [0x0410, 0x0119],
            [22] = [0x0205],
            [25] = [0x0118, 0x0411],
            [26] = [0x011A, 0x0412],
            [27] = [0x011C, 0x0413],
            [29] = [0x0112, 0x040B],
            [30] = [0x011E, 0x011F, 0x0415],
            [33] = [0x0121, 0x0417],
            [34] = [0x0122, 0x0418],
            [35] = [0x0419],
            [36] = [0x011D, 0x0414],
            [37] = [0x0123, 0x041A],
            [38] = [0x0124, 0x041B],
            [39] = [0x0125, 0x041C, 0x0127, 0x041D, 0x041F],
            [40] = [0x0421, 0x0126, 0x0129],
            [43] = [0x041E],
            [44] = [0x0420],
            [47] = [0x0422, 0x012A],
            [49] = [0x0423, 0x012B],
            [51] = [0x0424],
            [55] = [0x0425, 0x012E, 0x0426],
            [56] = [0x012F],
            [57] = [0x0427, 0x0130],
            [58] = [0x0131, 0x0428],
            [59] = [0x0429, 0x0132],
            [61] = [0x042D, 0x0133],
            [62] = [0x042E],
            [64] = [0x0136, 0x0433],
            [66] = [0x0431, 0x0134],
            [67] = [0x0432, 0x0135, 0x0435],
            [68] = [0x0117],
        };
        var map = new Dictionary<ushort, int>();
        foreach (var (bit, commands) in rows)
        {
            foreach (var command in commands) map.TryAdd(command, bit);
        }
        return map.ToFrozenDictionary();
    }
}
