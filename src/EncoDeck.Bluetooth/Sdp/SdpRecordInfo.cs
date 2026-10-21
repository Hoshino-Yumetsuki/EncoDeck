// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Bluetooth.Sdp;

/// <summary>Human-relevant fields decoded from one SDP service record.</summary>
public sealed record SdpRecordInfo(
    string? ServiceName,
    int? RfcommChannel,
    IReadOnlyList<Guid> ServiceClassIds,
    IReadOnlyDictionary<uint, byte[]> RawAttributes)
{
    public const uint ServiceClassIdListAttribute = 0x0001;
    public const uint ProtocolDescriptorListAttribute = 0x0004;
    public const uint ServiceNameAttribute = 0x0100;

    private static readonly Guid RfcommProtocolUuid = SdpDataElement.FromShort(0x0003);

    public static SdpRecordInfo FromRawAttributes(IReadOnlyDictionary<uint, byte[]> raw)
    {
        string? name = null;
        if (raw.TryGetValue(ServiceNameAttribute, out var nameBytes) && SdpDataElement.TryParse(nameBytes, out var nameElement))
        {
            name = nameElement!.AsText();
        }

        var classIds = new List<Guid>();
        if (raw.TryGetValue(ServiceClassIdListAttribute, out var classBytes) && SdpDataElement.TryParse(classBytes, out var classElement))
        {
            foreach (var child in classElement!.Children)
            {
                if (child.AsUuid() is { } id) classIds.Add(id);
            }
        }

        int? channel = null;
        if (raw.TryGetValue(ProtocolDescriptorListAttribute, out var protoBytes) && SdpDataElement.TryParse(protoBytes, out var protoElement))
        {
            channel = FindRfcommChannel(protoElement!);
        }

        return new SdpRecordInfo(name, channel, classIds, raw);
    }

    private static int? FindRfcommChannel(SdpDataElement protocolList)
    {
        foreach (var protocol in protocolList.Children)
        {
            if (protocol.Children.Count >= 2 && protocol.Children[0].AsUuid() == RfcommProtocolUuid)
            {
                return (int?)protocol.Children[1].AsUnsigned();
            }
            // Alternative-wrapped lists nest one level deeper.
            if (protocol.Type == SdpElementType.Sequence && protocol.Children.Count > 0 && protocol.Children[0].Type == SdpElementType.Sequence)
            {
                if (FindRfcommChannel(protocol) is { } nested) return nested;
            }
        }
        return null;
    }
}
