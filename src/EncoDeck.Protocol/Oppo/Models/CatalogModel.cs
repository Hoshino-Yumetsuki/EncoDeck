// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Oppo.Models;

/// <summary>One node of a model's noise-control tree: the official mode type and its protocol index.</summary>
internal sealed record CatalogNoiseNode(int ModeType, int ProtocolIndex, IReadOnlyList<CatalogNoiseNode> Children)
{
    /// <summary>Offered only when the earbuds report the mode as supported (<c>0x010C [03 01]</c>).</summary>
    public bool DecidedByDevice { get; init; }
}

/// <summary>One device EQ preset; <paramref name="MinFirmware"/> 0 = any firmware, <paramref name="ColorId"/> -1 = any colour.</summary>
internal sealed record CatalogEqualizer(int ModeType, int ProtocolIndex, int Order, int MinFirmware, int ColorId);

/// <summary>One gesture entry of the official configuration (<c>support</c> is a bitmask of offered functions).</summary>
internal sealed record CatalogControl(int Action, int Support, int ShowEar, int DefaultFunction, int MinSelectCount);

/// <summary>
/// The interoperability facts the official client keeps for one model (generated into
/// <see cref="OppoCatalog"/>), to be turned into an <see cref="OppoModelDefinition"/> the way the
/// official client interprets them.
/// </summary>
/// <param name="Type">Form factor of the official configuration: T1/T2 true wireless, O1 open, N neckband.</param>
internal sealed record CatalogModel(string ProductId, string Brand, string Type, IReadOnlyList<string> Names, string Uuid)
{
    public IReadOnlyList<CatalogNoiseNode> Noise { get; init; } = [];
    public IReadOnlyList<CatalogEqualizer> Equalizer { get; init; } = [];
    public IReadOnlyList<CatalogEqualizer> EqualizerCompat { get; init; } = [];
    public IReadOnlyList<CatalogEqualizer> EqualizerByVersion { get; init; } = [];
    public IReadOnlyList<CatalogControl> Controls { get; init; } = [];
    public IReadOnlyList<CatalogControl> CallControls { get; init; } = [];
    public IReadOnlyList<int> GameSoundTypes { get; init; } = [];
    public IReadOnlyList<string> MultiConnectFunctions { get; init; } = [];
    public (int Min, int Max)? PromptVolumeRange { get; init; }
    public IReadOnlyDictionary<string, int> Flags { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int[]> Lists { get; init; } = new Dictionary<string, int[]>();

    public string Name => Names[0];

    public uint ProductIdValue => Convert.ToUInt32(ProductId, 16);

    public int Flag(string name) => Flags.TryGetValue(name, out int value) ? value : 0;

    public bool Has(string name) => Flag(name) != 0;

    public IReadOnlyList<int> List(string name) => Lists.TryGetValue(name, out var value) ? value : [];
}
