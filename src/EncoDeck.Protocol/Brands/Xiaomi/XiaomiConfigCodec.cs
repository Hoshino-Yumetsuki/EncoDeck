// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>Official RCSP CommonConfig records: length (including two type bytes), type BE, value.</summary>
public static class XiaomiConfigCodec
{
    public static byte[] ReadPayload(params ushort[] ids) => ids.SelectMany(id => new byte[] { (byte)(id >> 8), (byte)id }).ToArray();
    public static byte[] WritePayload(ushort id, ReadOnlySpan<byte> value)
    {
        if (value.Length > 252) throw new ArgumentOutOfRangeException(nameof(value));
        return [(byte)(value.Length + 2), (byte)(id >> 8), (byte)id, .. value];
    }
    public static bool TryParse(ReadOnlySpan<byte> payload, out IReadOnlyDictionary<ushort, byte[]> values)
    {
        var result = new Dictionary<ushort, byte[]>(); values = result;
        while (!payload.IsEmpty)
        {
            int length = payload[0];
            if (length < 2 || length == 255 || payload.Length < length + 1) return false;
            ushort id = (ushort)((payload[1] << 8) | payload[2]);
            // Duplicate records are ambiguous; never use a partial/invalid packet to enable a feature.
            if (!result.TryAdd(id, payload.Slice(3, length - 2).ToArray())) return false;
            payload = payload[(length + 1)..];
        }
        return result.Count > 0;
    }
    public static XiaomiFrame? NormalizeResponse(XiaomiFrame frame)
    {
        if ((frame.Kind & 0x80) != 0) return frame;
        // The byte the legacy decoder calls Sequence is the response status. The real sequence follows.
        if (frame.Sequence != 0 || frame.Payload.Length == 0) return null;
        return frame with { Sequence = frame.Payload[0], Payload = frame.Payload[1..] };
    }
}
