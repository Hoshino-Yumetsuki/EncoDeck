// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text;

namespace EncoDeck.Bluetooth.Sdp;

public enum SdpElementType : byte
{
    Nil = 0,
    UnsignedInteger = 1,
    SignedInteger = 2,
    Uuid = 3,
    Text = 4,
    Boolean = 5,
    Sequence = 6,
    Alternative = 7,
    Url = 8,
}

/// <summary>
/// Minimal parser for SDP data elements (Bluetooth Core Spec Vol 3, Part B, §3).
/// Used only to interpret raw attributes returned by <c>GetSdpRawAttributesAsync</c>.
/// </summary>
public sealed class SdpDataElement
{
    private SdpDataElement(SdpElementType type, ReadOnlyMemory<byte> value, IReadOnlyList<SdpDataElement> children)
    {
        Type = type;
        Value = value;
        Children = children;
    }

    public SdpElementType Type { get; }
    public ReadOnlyMemory<byte> Value { get; }
    public IReadOnlyList<SdpDataElement> Children { get; }

    public ulong? AsUnsigned() => Type == SdpElementType.UnsignedInteger ? ReadUnsigned(Value.Span) : null;

    public string? AsText() => Type is SdpElementType.Text or SdpElementType.Url
        ? Encoding.UTF8.GetString(Value.Span).TrimEnd('\0')
        : null;

    public Guid? AsUuid()
    {
        if (Type != SdpElementType.Uuid) return null;
        var span = Value.Span;
        return span.Length switch
        {
            2 => FromShort(BinaryPrimitives.ReadUInt16BigEndian(span)),
            4 => FromShort(BinaryPrimitives.ReadUInt32BigEndian(span)),
            16 => new Guid(span, bigEndian: true),
            _ => null,
        };
    }

    /// <summary>Expands a 16/32-bit SIG UUID onto the Bluetooth base UUID.</summary>
    public static Guid FromShort(uint shortUuid) =>
        new($"{shortUuid:X8}-0000-1000-8000-00805F9B34FB");

    public static bool TryParse(ReadOnlyMemory<byte> data, out SdpDataElement? element)
    {
        try
        {
            element = Parse(data, out _);
            return true;
        }
        catch (FormatException)
        {
            element = null;
            return false;
        }
    }

    private static SdpDataElement Parse(ReadOnlyMemory<byte> data, out int consumed)
    {
        var span = data.Span;
        if (span.Length < 1) throw new FormatException("Empty SDP element.");
        var type = (SdpElementType)(span[0] >> 3);
        int sizeIndex = span[0] & 0x07;
        int headerLength = 1;
        int length;
        if (type == SdpElementType.Nil)
        {
            length = 0;
        }
        else if (sizeIndex <= 4)
        {
            length = sizeIndex switch { 0 => 1, 1 => 2, 2 => 4, 3 => 8, _ => 16 };
        }
        else
        {
            int lengthBytes = sizeIndex switch { 5 => 1, 6 => 2, _ => 4 };
            if (span.Length < 1 + lengthBytes) throw new FormatException("Truncated SDP length.");
            length = lengthBytes switch
            {
                1 => span[1],
                2 => BinaryPrimitives.ReadUInt16BigEndian(span[1..]),
                _ => checked((int)BinaryPrimitives.ReadUInt32BigEndian(span[1..])),
            };
            headerLength += lengthBytes;
        }

        if (span.Length < headerLength + length) throw new FormatException("Truncated SDP value.");
        var value = data.Slice(headerLength, length);
        consumed = headerLength + length;

        if (type is SdpElementType.Sequence or SdpElementType.Alternative)
        {
            var children = new List<SdpDataElement>();
            int offset = 0;
            while (offset < length)
            {
                children.Add(Parse(value[offset..], out int used));
                offset += used;
            }
            return new SdpDataElement(type, value, children);
        }

        return new SdpDataElement(type, value, []);
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> span) => span.Length switch
    {
        1 => span[0],
        2 => BinaryPrimitives.ReadUInt16BigEndian(span),
        4 => BinaryPrimitives.ReadUInt32BigEndian(span),
        8 => BinaryPrimitives.ReadUInt64BigEndian(span),
        _ => 0,
    };
}
