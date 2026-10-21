// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using EncoDeck.App.Services;
using EncoDeck.Core.State;

namespace EncoDeck.App.ViewModels;

/// <summary>One battery (left, right or case) as shown in the UI.</summary>
public sealed partial class BatteryItemViewModel(BatteryComponent component) : ObservableObject
{
    /// <summary>Readings older than this, or restored from cache, are labelled "last read".</summary>
    public static readonly TimeSpan LiveThreshold = TimeSpan.FromMinutes(10);

    public BatteryComponent Component { get; } = component;

    public string Label { get; } = component switch
    {
        BatteryComponent.Left => Loc.Get("Battery_Left"),
        BatteryComponent.Right => Loc.Get("Battery_Right"),
        _ => Loc.Get("Battery_Case"),
    };

    [ObservableProperty]
    public partial bool HasValue { get; set; }

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string LevelText { get; set; } = "--";

    [ObservableProperty]
    public partial bool IsCharging { get; set; }

    [ObservableProperty]
    public partial bool IsStale { get; set; }

    /// <summary>"Last read 10:21" for stale values, empty for live ones.</summary>
    [ObservableProperty]
    public partial string FreshnessText { get; set; } = string.Empty;

    /// <summary>Segoe Fluent Icons battery glyph matching the level.</summary>
    [ObservableProperty]
    public partial string Glyph { get; set; } = "";

    [ObservableProperty]
    public partial string AutomationText { get; set; } = string.Empty;

    public void Apply(Observed<BatteryReading>? reading, bool channelReady, DateTimeOffset now)
    {
        if (reading?.Value.Level is not { } level)
        {
            HasValue = false;
            Percent = 0;
            LevelText = "--";
            IsCharging = false;
            IsStale = false;
            FreshnessText = Loc.Get("Battery_Unknown");
            Glyph = "";
            AutomationText = $"{Label} {Loc.Get("Battery_Unknown")}";
            return;
        }

        bool stale = !channelReady || reading.IsStale(now, LiveThreshold);
        HasValue = true;
        Percent = level;
        LevelText = $"{level}%";
        IsCharging = reading.Value.IsCharging == true && !stale;
        IsStale = stale;
        FreshnessText = stale ? Loc.Format("Battery_LastRead", reading.Timestamp.ToLocalTime().ToString("t")) : string.Empty;
        int step = Math.Clamp((int)Math.Round(level / 10.0), 0, 10);
        Glyph = ((char)((IsCharging ? 0xEBAB : 0xEBA0) + step)).ToString();
        AutomationText = $"{Label} {LevelText}{(IsCharging ? " " + Loc.Get("Battery_Charging") : string.Empty)}{(stale ? " " + FreshnessText : string.Empty)}";
    }
}
