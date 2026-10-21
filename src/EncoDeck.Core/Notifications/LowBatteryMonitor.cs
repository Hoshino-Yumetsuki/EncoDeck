// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;

namespace EncoDeck.Core.Notifications;

/// <summary>
/// Emits a low-battery notice once per discharge: when an earbud's live, non-charging level drops
/// to the threshold. It re-arms after charging or once the level is clearly above the threshold
/// again, so small fluctuations around the threshold do not repeat the notice.
/// </summary>
public sealed class LowBatteryMonitor
{
    public const int Hysteresis = 5;

    private readonly HashSet<BatteryComponent> _notified = [];
    private readonly Lock _gate = new();

    /// <summary>Returns the earbuds that just crossed into the low state.</summary>
    public IReadOnlyList<(BatteryComponent Component, int Level)> Evaluate(BatteryStatus status, int threshold)
    {
        var result = new List<(BatteryComponent, int)>();
        lock (_gate)
        {
            foreach (var component in new[] { BatteryComponent.Left, BatteryComponent.Right })
            {
                var reading = status.Get(component);
                if (reading is null || reading.Source == StateSource.Cache || !reading.Value.HasLevel) continue;
                int level = reading.Value.Level!.Value;
                bool charging = reading.Value.IsCharging == true;

                if (charging || level > threshold + Hysteresis)
                {
                    _notified.Remove(component);
                }
                else if (level <= threshold && _notified.Add(component))
                {
                    result.Add((component, level));
                }
            }
        }
        return result;
    }

    public void Reset()
    {
        lock (_gate) _notified.Clear();
    }
}
