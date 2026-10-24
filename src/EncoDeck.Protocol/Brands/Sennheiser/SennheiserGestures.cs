// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sennheiser;

/// <summary>TW4 MMI facts from Smart Control 4.9.3 Tw4Product and its MMI enums.</summary>
public static class SennheiserGestures
{
    public static IReadOnlyList<GestureAction> Actions { get; } =
        [GestureAction.SingleSqueeze, GestureAction.DoubleSqueeze, GestureAction.TripleSqueeze, GestureAction.SqueezeAndHold];

    public static IReadOnlyList<int> Functions(GestureAction action) => action switch
    {
        GestureAction.SingleSqueeze or GestureAction.DoubleSqueeze => [0, 1, 2, 3, 6, 7, 9],
        GestureAction.TripleSqueeze => [0, 1, 2, 3, 6, 7, 9, 13],
        GestureAction.SqueezeAndHold => [0, 4, 5, 6, 7, 9, 13],
        _ => [],
    };

    public static string LabelKey(int function) => function switch
    {
        0 => "Function_0", 1 => "Function_1", 2 => "Function_6", 3 => "Function_5",
        4 => "Function_11", 5 => "Function_12", 6 => "Sennheiser_ToggleTransparency",
        7 => "Function_3", 9 => "Sennheiser_ToggleAnc", 13 => "Sennheiser_ToggleTouchLock",
        _ => "",
    };

    internal static bool ValidSlot(byte[] payload) => payload is [<= 1, >= 1 and <= 4, _];
    internal static byte WireSide(EarSide side) => side == EarSide.Left ? (byte)1 : (byte)0;
}
