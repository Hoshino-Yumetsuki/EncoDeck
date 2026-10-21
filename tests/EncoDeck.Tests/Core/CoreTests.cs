// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Configuration;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Notifications;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;

namespace EncoDeck.Tests.Core;

public class StateStoreTests
{
    private static readonly DeviceIdentity A = new("dev-a", "OPPO Enco X4", new BluetoothAddress(1));

    [Fact]
    public void Update_RaisesEventWithIncrementingRevision()
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = A });
        var events = new List<DeviceStateChangedEventArgs>();
        store.StateChanged += (_, e) => events.Add(e);

        store.TryUpdate("dev-a", s => s with { Connection = DeviceConnectionState.ControlReady });

        var e = Assert.Single(events);
        Assert.Equal(DeviceConnectionState.ControlReady, e.Current.Connection);
        Assert.Equal(e.Previous.Revision + 1, e.Current.Revision);
    }

    [Fact]
    public void NoOpUpdate_DoesNotRaiseEvent()
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = A });
        int count = 0;
        store.StateChanged += (_, _) => count++;
        store.TryUpdate("dev-a", s => s with { Connection = s.Connection });
        Assert.Equal(0, count);
    }

    [Fact]
    public void UpdateForOtherDevice_IsDropped()
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = A });
        Assert.False(store.TryUpdate("dev-b", s => s with { Connection = DeviceConnectionState.ControlReady }));
        Assert.Equal(DeviceConnectionState.NotDetected, store.Current.Connection);
    }

    [Fact]
    public void ConcurrentUpdates_AreAllApplied()
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = A });
        Parallel.For(0, 500, _ => store.TryUpdate(null, s => s with { Revision = s.Revision, PendingCommands = s.PendingCommands.Add(new EncoDeck.Core.Commands.PendingCommand(Guid.NewGuid(), "x", default)) }));
        Assert.Equal(500, store.Current.PendingCommands.Count);
    }
}

public class ObservedTests
{
    [Fact]
    public void CacheIsAlwaysStale_LiveIsStaleOnlyWhenOld()
    {
        var now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        var cached = Observed.From(1, now, StateSource.Cache);
        var live = Observed.From(1, now.AddMinutes(-2), StateSource.DeviceNotification);
        var old = Observed.From(1, now.AddMinutes(-20), StateSource.QueryResponse);
        Assert.True(cached.IsStale(now, TimeSpan.FromMinutes(10)));
        Assert.False(live.IsStale(now, TimeSpan.FromMinutes(10)));
        Assert.True(old.IsStale(now, TimeSpan.FromMinutes(10)));
        Assert.False(cached.IsConfirmed);
        Assert.True(live.IsConfirmed);
    }

    [Fact]
    public void LowestEarbudLevel_IgnoresUnknown()
    {
        var t = DateTimeOffset.UtcNow;
        var status = BatteryStatus.Unknown with
        {
            Left = Observed.From(new BatteryReading(40, false), t, StateSource.QueryResponse),
            Right = Observed.From(BatteryReading.Unknown, t, StateSource.QueryResponse),
            Case = Observed.From(new BatteryReading(5, false), t, StateSource.QueryResponse),
        };
        Assert.Equal(40, status.LowestEarbudLevel);
        Assert.Null(BatteryStatus.Unknown.LowestEarbudLevel);
    }
}

public class CapabilityTests
{
    [Fact]
    public void Availability_RespectsSupportLevelAndOptIn()
    {
        var caps = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "hw")
            .Set(DeviceCapability.Equalizer, CapabilitySupport.Experimental, "doc")
            .Set(DeviceCapability.SpatialAudio, CapabilitySupport.Unsupported, "no")
            .Build();

        Assert.True(caps.IsAvailable(DeviceCapability.BatteryLevel, allowExperimental: false));
        Assert.False(caps.IsAvailable(DeviceCapability.Equalizer, allowExperimental: false));
        Assert.True(caps.IsAvailable(DeviceCapability.Equalizer, allowExperimental: true));
        Assert.False(caps.IsAvailable(DeviceCapability.SpatialAudio, allowExperimental: true));
        // Anything not declared is Unknown and never available.
        Assert.Equal(CapabilitySupport.Unknown, caps.Get(DeviceCapability.HeadTracking).Support);
        Assert.False(caps.IsAvailable(DeviceCapability.HeadTracking, allowExperimental: true));
        Assert.Equal(Enum.GetValues<DeviceCapability>().Length, caps.All.Count());
    }

    [Fact]
    public void With_ReplacesOneEntry()
    {
        var caps = DeviceCapabilities.Empty.With(DeviceCapability.GamingMode, CapabilitySupport.Supported, "verified");
        Assert.Equal(CapabilitySupport.Supported, caps.Get(DeviceCapability.GamingMode).Support);
        Assert.Equal(CapabilitySupport.Unknown, DeviceCapabilities.Empty.Get(DeviceCapability.GamingMode).Support);
    }
}

public class ConnectionPopupPolicyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-20T12:00:00Z");

    [Fact]
    public void ShowsOncePerConnection_AndSuppressesJitter()
    {
        var policy = new ConnectionPopupPolicy { Cooldown = TimeSpan.FromSeconds(90) };
        var settings = new AppSettings();
        Assert.Equal(PopupDecision.Show, policy.Evaluate("d", T0, settings, false, false));
        policy.RecordShown("d", T0);
        Assert.Equal(PopupDecision.Cooldown, policy.Evaluate("d", T0.AddSeconds(5), settings, false, false));
        Assert.Equal(PopupDecision.Show, policy.Evaluate("other", T0.AddSeconds(5), settings, false, false));
        Assert.Equal(PopupDecision.Show, policy.Evaluate("d", T0.AddSeconds(91), settings, false, false));
    }

    [Fact]
    public void RespectsSettingsAndInterruptions()
    {
        var policy = new ConnectionPopupPolicy();
        Assert.Equal(PopupDecision.Disabled, policy.Evaluate("d", T0, new AppSettings { ShowConnectionPopup = false }, false, false));
        Assert.Equal(PopupDecision.SuppressedFullscreen, policy.Evaluate("d", T0, new AppSettings(), isFullscreen: true, isAway: false));
        Assert.Equal(PopupDecision.SuppressedAway, policy.Evaluate("d", T0, new AppSettings(), isFullscreen: false, isAway: true));
    }

    [Fact]
    public void Fullscreen_WhenAllowed_ShowsAsClickThroughOverlay()
    {
        var policy = new ConnectionPopupPolicy();
        var allowed = new AppSettings { SuppressPopupWhenFullscreen = false };
        Assert.Equal(PopupDecision.ShowPassThrough, policy.Evaluate("d", T0, allowed, isFullscreen: true, isAway: false));
        Assert.Equal(PopupDecision.Show, policy.Evaluate("d", T0, allowed, isFullscreen: false, isAway: false));
    }

    [Fact]
    public void ExclusiveFullscreen_IsNeverInterrupted()
    {
        var policy = new ConnectionPopupPolicy();
        var allowed = new AppSettings { SuppressPopupWhenFullscreen = false };
        Assert.Equal(PopupDecision.SuppressedFullscreen, policy.Evaluate("d", T0, allowed, isFullscreen: true, isAway: false, isExclusiveFullscreen: true));
    }

    [Fact]
    public void CaseLidOpened_UsesTheShortOfficialDebounce()
    {
        var policy = new ConnectionPopupPolicy();
        var settings = new AppSettings();
        policy.RecordShown("d", T0);
        Assert.Equal(PopupDecision.Cooldown, policy.Evaluate("d", T0.AddSeconds(3), settings, false, false, trigger: PopupTrigger.CaseLidOpened));
        Assert.Equal(PopupDecision.Show, policy.Evaluate("d", T0.AddSeconds(6), settings, false, false, trigger: PopupTrigger.CaseLidOpened));
    }

    [Fact]
    public void Reconnect_AfterTheOfficialDebounce_PopsUpAgain()
    {
        var policy = new ConnectionPopupPolicy();
        var settings = new AppSettings();
        policy.RecordShown("d", T0);
        Assert.Equal(PopupDecision.Cooldown, policy.Evaluate("d", T0.AddSeconds(2), settings, false, false));
        Assert.Equal(PopupDecision.Show, policy.Evaluate("d", T0.AddSeconds(6), settings, false, false));
    }
}

public class LowBatteryMonitorTests
{
    private static BatteryStatus Status(int left, bool charging = false, StateSource source = StateSource.DeviceNotification) =>
        BatteryStatus.Unknown with { Left = Observed.From(new BatteryReading(left, charging), DateTimeOffset.UtcNow, source) };

    [Fact]
    public void NotifiesOncePerDischarge_WithHysteresis()
    {
        var monitor = new LowBatteryMonitor();
        Assert.Empty(monitor.Evaluate(Status(50), 20));
        Assert.Single(monitor.Evaluate(Status(20), 20));
        Assert.Empty(monitor.Evaluate(Status(10), 20));
        Assert.Empty(monitor.Evaluate(Status(22), 20)); // still within hysteresis
        Assert.Empty(monitor.Evaluate(Status(19), 20));
        Assert.Empty(monitor.Evaluate(Status(30), 20)); // re-armed
        Assert.Single(monitor.Evaluate(Status(18), 20));
    }

    [Fact]
    public void ChargingRearms_AndCachedValuesAreIgnored()
    {
        var monitor = new LowBatteryMonitor();
        Assert.Empty(monitor.Evaluate(Status(5, source: StateSource.Cache), 20));
        Assert.Single(monitor.Evaluate(Status(10), 20));
        Assert.Empty(monitor.Evaluate(Status(11, charging: true), 20));
        Assert.Single(monitor.Evaluate(Status(10), 20));
    }
}

public class AppSettingsTests
{
    [Fact]
    public void Normalize_ClampsRanges()
    {
        var s = new AppSettings { PopupDurationSeconds = 999, LowBatteryThreshold = 1 }.Normalize();
        Assert.Equal(30, s.PopupDurationSeconds);
        Assert.Equal(5, s.LowBatteryThreshold);
    }
}

public class BluetoothAddressTests
{
    [Fact]
    public void Formats_RedactsByDefault()
    {
        var address = new BluetoothAddress(0xE498E0123456);
        Assert.Equal("E4:98:E0:12:34:56", address.ToFullString());
        Assert.Equal("**:**:**:**:34:56", address.ToRedactedString());
        Assert.Equal("**:**:**:**:34:56", address.ToString());
        Assert.StartsWith("dev-", address.ToPseudonym("salt"));
        Assert.NotEqual(address.ToPseudonym("a"), address.ToPseudonym("b"));
    }
}
