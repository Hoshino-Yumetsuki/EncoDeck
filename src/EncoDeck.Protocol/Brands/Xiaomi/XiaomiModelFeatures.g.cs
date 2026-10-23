// SPDX-License-Identifier: GPL-3.0-only
// Generated from official Xiaomi Earbuds regional product catalogs.
#nullable enable
namespace EncoDeck.Protocol.Brands.Xiaomi;
internal sealed record XiaomiModelFeatures(string Name, byte[] NoiseLevels, byte[] AmbientLevels, byte[] Equalizers)
{
    internal static XiaomiModelFeatures? Find(string? name) => All.FirstOrDefault(m => Normalize(m.Name) == Normalize(name));
    private static string Normalize(string? name) => new((name ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static readonly XiaomiModelFeatures[] All =
    [
        new("REDMI Buds 6 Pro\u00a0\u7535\u7ade\u7248", [], [0, 1, 2], [0, 1, 5, 6]),
        new("REDMI Buds 7S", [0, 2], [], [0, 1, 5, 6]),
        new("REDMI Buds 8", [0, 1, 2], [0, 1, 2], [1, 5, 6, 7, 21]),
        new("REDMI Buds 8 Active", [], [], [1, 5, 6, 7, 21]),
        new("REDMI Buds 8 Lite", [0], [0], [1, 5, 6, 7, 21]),
        new("REDMI Buds 8 Pro", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19], [0, 1, 2], [0, 1, 5, 6]),
        new("REDMI Buds 8 \u6d3b\u529b\u7248", [], [], [1, 5, 6, 7, 21]),
        new("REDMI Buds 8 \u9752\u6625\u7248", [0], [0], [1, 5, 6, 7, 21]),
        new("REDMI Buds 8S", [0, 2], [], [1, 5, 6, 7, 21]),
        new("REDMI Headphones Neo", [0, 1, 2], [0], [1, 5, 6, 21]),
        new("REDMI \u5934\u6234\u964d\u566a\u8033\u673a", [0, 1, 2], [0], [1, 5, 6, 21]),
        new("REDMI\u00a0Buds\u00a06\u00a0Pro", [], [0, 1, 2], [0, 1, 5, 6]),
        new("REDMI\u00a0Buds\u00a0SE", [], [], [0, 1, 5, 6, 7]),
        new("Redmi Buds 4", [0, 1, 2], [], []),
        new("Redmi Buds 4 Active", [], [], []),
        new("Redmi Buds 4 Pro", [0, 1, 2], [], []),
        new("Redmi Buds 4 \u54c8\u5229\u00b7\u6ce2\u7279\u7248", [0, 1, 2], [], []),
        new("Redmi Buds 4\u6d3b\u529b\u7248", [], [], []),
        new("Redmi Buds 5", [0, 1, 2], [0, 1, 2], [0, 1, 5, 6]),
        new("Redmi Buds 5 AAPE", [0, 1, 2], [0, 1, 2], [0, 1, 5, 6]),
        new("Redmi Buds 5 Pro", [0, 1, 2], [0, 1, 2], [0, 1, 5, 6]),
        new("Redmi Buds 5 Pro Gaming", [0, 1, 2], [0, 1, 2], [0, 1, 5, 6]),
        new("Redmi Buds 6 Active", [], [], [0, 1, 5, 6, 7]),
        new("Redmi Buds 6 Lite", [0], [0], [0, 1, 5, 6]),
        new("Redmi Buds 6 Play", [], [], [0, 1, 5, 6, 7]),
        new("Redmi Buds 6 \u6d3b\u529b\u7248", [], [], [0, 1, 5, 6, 7]),
        new("Redmi Buds 6 \u9752\u6625\u7248", [0], [0], [0, 1, 5, 6]),
        new("Redmi Buds 6S", [0, 2], [], [0, 1, 5, 6]),
        new("Redmi\u00a0Buds\u00a06", [0, 1, 2], [0, 1, 2], [0, 1, 5, 6]),
        new("Xiaomi Air 5", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19], [0, 1, 2], [1, 5, 6, 21]),
        new("Xiaomi Buds 3", [], [], []),
        new("Xiaomi Buds 3 Pro", [0, 1, 2], [], []),
        new("Xiaomi Buds 3 Star Wars", [], [], []),
        new("Xiaomi Buds 3T Pro", [0, 1, 2], [], []),
        new("Xiaomi Buds 4", [], [], []),
        new("Xiaomi Buds 4 Pro", [], [0, 1, 2], []),
        new("Xiaomi Buds 5", [0, 2], [], [0, 1, 6, 11, 13]),
        new("Xiaomi Buds 6", [0, 2], [], [1, 6, 13, 14, 15, 21]),
        new("Xiaomi OpenWear Stereo", [], [], [0, 1, 6]),
        new("Xiaomi OpenWear Stereo Pro", [], [], [1, 6, 14, 15, 21]),
        new("Xiaomi \u5f00\u653e\u5f0f\u8033\u673a", [], [], [0, 1, 6]),
        new("Xiaomi \u5f00\u653e\u5f0f\u8033\u673a Pro", [], [], [1, 6, 14, 15, 21]),
        new("Xiaomi \u771f\u65e0\u7ebf\u964d\u566a\u8033\u673a 3 \u8fea\u58eb\u5c3c100\u5468\u5e74\u9650\u5b9a\u7248", [], [], []),
        new("Xiaomi \u8033\u5939\u5f0f\u8033\u673a", [], [], [1, 6, 21]),
        new("Xiaomi \u9aa8\u4f20\u5bfc\u8033\u673a2", [], [], [16, 17, 18]),
        new("Xiaomi\u00a0Buds\u00a05\u00a0Pro", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19], [0, 1, 2], [1, 6, 12, 13, 14, 15]),
        new("Xiaomi\u00a0Buds\u00a05\u00a0Pro\u00a0Wi-Fi", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19], [0, 1, 2], [1, 6, 12, 13, 14, 15]),
        new("\u5c0f\u7c73\u00a0Air4\u00a0SE", [], [], [0, 1, 5, 6, 7]),
    ];
}
