// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Huawei;

namespace EncoDeck.Tests.Protocol.Huawei;

public class HuaweiCatalogTests
{
    private static DeviceIdentity Named(string name) => new("dev", name, new BluetoothAddress(0x112233445566));

    [Fact]
    public void BuiltIn_HasEveryModelAndOneGeneric()
    {
        var profiles = HuaweiProfiles.CreateBuiltIn().Cast<IControllableProfile>().ToList();
        Assert.Equal(25, profiles.Count);
        Assert.Single(profiles, p => p.IsGeneric);
        Assert.All(profiles, p =>
        {
            Assert.Equal("huawei", p.Brand);
            Assert.StartsWith("huawei-", p.ModelId);
            Assert.Equal(p.ModelId, p.ImageKey);
            Assert.Equal([HuaweiCommands.SppServiceUuid], p.ControlServiceUuids);
        });
        Assert.Equal(profiles.Count, profiles.Select(p => p.ModelId).Distinct().Count());
    }

    [Fact]
    public void Profiles_DeclareOnlyWhatTheyOffer()
    {
        foreach (var p in HuaweiProfiles.CreateBuiltIn().Cast<HuaweiDeviceProfile>())
        {
            Assert.Equal(p.NoiseControlOptions.Count > 0, p.Capabilities.Get(DeviceCapability.NoiseCancellation).Support == CapabilitySupport.Supported);
            Assert.Equal(p.EqualizerPresets.Count > 0, p.Capabilities.Get(DeviceCapability.Equalizer).Support == CapabilitySupport.Supported);
            Assert.Equal(p.EqualizerPresets.Count, p.EqualizerPresets.Select(e => e.Id).Distinct().Count());
            Assert.All(p.NoiseControlOptions, o => Assert.False(string.IsNullOrEmpty(o.ResourceKey)));
        }
    }

    [Theory]
    [InlineData("HUAWEI FreeBuds Pro 3", "huawei-freebuds-pro-3", 100)]
    [InlineData("FreeBuds Pro 3", "huawei-freebuds-pro-3", 100)]
    [InlineData("HUAWEI FreeBuds Pro", "huawei-freebuds-pro", 100)]
    [InlineData("HUAWEI FreeBuds Pro 3 (A1B2)", "huawei-freebuds-pro-3", 50)]
    [InlineData("HUAWEI FreeBuds 5i", "huawei-freebuds-5i", 100)]
    [InlineData("HUAWEI FreeLace Pro 2", "huawei-freelace-pro-2", 100)]
    [InlineData("HUAWEI Eyewear 2", "huawei-eyewear-2", 100)]
    [InlineData("HUAWEI FreeBuds X9", "huawei-generic", 1)]
    public void Registry_MatchesByName(string name, string expected, int score)
    {
        var registry = new DeviceProfileRegistry(HuaweiProfiles.CreateBuiltIn());
        var match = registry.Match(Named(name));
        Assert.NotNull(match);
        Assert.Equal(expected, match.ModelId);
        Assert.Equal(score, match.MatchScore(Named(name)));
    }

    [Fact]
    public void Registry_IgnoresOtherBrands()
    {
        var registry = new DeviceProfileRegistry(HuaweiProfiles.CreateBuiltIn());
        Assert.Null(registry.Match(Named("OPPO Enco X4")));
    }

    [Fact]
    public void Pro3_UsesProEqIdsAndFourLevels()
    {
        var model = HuaweiModels.All.Single(m => m.ModelId == "huawei-freebuds-pro-3");
        Assert.Equal(5, model.EqPresets.Single(p => p.PresetId == "default").ProtocolId);
        Assert.Equal(["dynamic", "cozy", "general", "ultra"], model.AncLevels.Select(l => l.LevelId));
        Assert.Equal(HuaweiProtocol.Legacy, model.Protocol);
    }
}
