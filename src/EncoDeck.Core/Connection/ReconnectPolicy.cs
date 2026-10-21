// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Connection;

/// <summary>
/// Bounded exponential backoff. After <see cref="MaxAttempts"/> failures the channel is reported
/// as unavailable and only a user action (or a fresh Bluetooth connection event) restarts it.
/// </summary>
public sealed record ReconnectPolicy(TimeSpan InitialDelay, double Multiplier, TimeSpan MaxDelay, int MaxAttempts)
{
    public static ReconnectPolicy Default { get; } = new(TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromSeconds(30), 6);

    /// <summary>Delay before retry number <paramref name="attempt"/> (1-based), or null when retries are exhausted.</summary>
    public TimeSpan? GetDelay(int attempt)
    {
        if (attempt < 1 || attempt > MaxAttempts) return null;
        double ms = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelay.TotalMilliseconds));
    }
}
