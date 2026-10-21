// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

public enum BatteryComponent
{
    Left,
    Right,
    Case,
}

/// <summary>One battery reading. <see cref="Level"/> is 0–100; null fields are unknown.</summary>
public sealed record BatteryReading(int? Level, bool? IsCharging)
{
    public static BatteryReading Unknown { get; } = new(null, null);

    public bool HasLevel => Level is >= 0 and <= 100;
}

/// <summary>Battery state of the three components. Each one is timestamped independently
/// because the case level, for example, is typically only reported while a bud is docked.</summary>
public sealed record BatteryStatus(
    Observed<BatteryReading>? Left,
    Observed<BatteryReading>? Right,
    Observed<BatteryReading>? Case)
{
    public static BatteryStatus Unknown { get; } = new(null, null, null);

    public Observed<BatteryReading>? Get(BatteryComponent component) => component switch
    {
        BatteryComponent.Left => Left,
        BatteryComponent.Right => Right,
        _ => Case,
    };

    public BatteryStatus With(BatteryComponent component, Observed<BatteryReading>? reading) => component switch
    {
        BatteryComponent.Left => this with { Left = reading },
        BatteryComponent.Right => this with { Right = reading },
        _ => this with { Case = reading },
    };

    /// <summary>Lowest known earbud level, used for the tray tooltip and low-battery notices.</summary>
    public int? LowestEarbudLevel
    {
        get
        {
            int? l = Left?.Value.HasLevel == true ? Left.Value.Level : null;
            int? r = Right?.Value.HasLevel == true ? Right.Value.Level : null;
            return (l, r) switch
            {
                (null, null) => null,
                (null, _) => r,
                (_, null) => l,
                _ => Math.Min(l!.Value, r!.Value),
            };
        }
    }
}
