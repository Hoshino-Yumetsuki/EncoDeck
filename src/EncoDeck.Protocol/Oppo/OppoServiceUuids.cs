// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Oppo;

/// <summary>
/// Candidate RFCOMM service UUIDs for the OPPO/HeyMelody control channel.
/// These come from community research (see docs/PROTOCOL_FINDINGS.md). They are candidates
/// until the device's SDP database confirms them — never assume a device exposes them.
/// </summary>
public static class OppoServiceUuids
{
    public static readonly Guid Primary = new("00001107-D102-11E1-9B23-00025B00A5A5");
    public static readonly Guid Secondary = new("0000079A-D102-11E1-9B23-00025B00A5A5");

    /// <summary>Candidates in the order they should be tried.</summary>
    public static IReadOnlyList<Guid> Candidates { get; } = [Primary, Secondary];

    public static bool IsCandidate(Guid uuid) => Candidates.Contains(uuid);
}
