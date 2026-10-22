// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Lhdc;

namespace EncoDeck.Tests.Platform;

public sealed class LhdcBackendTests
{
    [Theory]
    [InlineData(22050, 24, 400)]
    [InlineData(48000, 32, 400)]
    [InlineData(48000, 24, -1)]
    public void RejectsUnsupportedFormatsBeforeStartingDeployment(int rate, int bits, int kbps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LhdcBackend.ModeCommand("C:/runtime", "BTHENUM\\device", true, new(rate, bits, kbps), "C:/result.json"));

    [Fact]
    public void QuotesDeviceAndPathsAsLiterals()
    {
        string command = LhdcBackend.ModeCommand("C:/O'Brien/runtime", "BTHENUM\\device'; Write-Host injected; 'x", true, new(), "C:/O'Brien/result.json");
        Assert.Contains("C:/O''Brien", command);
        Assert.Contains("-DeviceInstance 'BTHENUM\\device''; Write-Host injected; ''x'", command);
        Assert.Contains("-Mode LHDC", command);
    }

    [Fact]
    public async Task QueryCannotInvokeMutatingCommands()
    {
        var backend = new LhdcBackend("missing", "missing");
        await Assert.ThrowsAsync<ArgumentException>(() => backend.QueryAsync(null, "hires", "on"));
    }
}
