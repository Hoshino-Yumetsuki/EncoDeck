// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands.Edifier;
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Brands.Vivo;
using EncoDeck.Protocol.Brands.Xiaomi;
using EncoDeck.Protocol.Oppo.Models;

namespace EncoDeck.Protocol.Brands;

/// <summary>Every model profile EncoDeck ships, across all brand protocols.</summary>
public static class BuiltInProfiles
{
    public static IReadOnlyList<IDeviceProfile> Create() =>
    [
        .. OppoDeviceProfile.CreateBuiltIn(),
        .. EdifierProfiles.CreateBuiltIn(),
        .. XiaomiProfiles.CreateBuiltIn(),
        .. VivoProfiles.CreateBuiltIn(),
        .. HuaweiProfiles.CreateBuiltIn(),
    ];
}
