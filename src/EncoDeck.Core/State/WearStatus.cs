// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

/// <summary>Where each earbud is. Null fields are unknown.</summary>
public sealed record WearStatus(
    bool? LeftInEar,
    bool? RightInEar,
    bool? LeftInCase,
    bool? RightInCase,
    bool? CaseLidOpen)
{
    public static WearStatus Unknown { get; } = new(null, null, null, null, null);

    public bool AnyInEar => LeftInEar == true || RightInEar == true;

    public bool BothInCase => LeftInCase == true && RightInCase == true;
}
