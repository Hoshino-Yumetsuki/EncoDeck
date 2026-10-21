// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Interop;

namespace EncoDeck.Platform.Shell;

public enum InterruptionState
{
    /// <summary>Safe to show transient UI.</summary>
    Available,
    /// <summary>A fullscreen (borderless) app or game is in the foreground, or the user is busy.</summary>
    Fullscreen,
    /// <summary>
    /// Exclusive (D3D) fullscreen or presentation mode. Nothing may be shown: a topmost window
    /// can knock a game out of exclusive fullscreen.
    /// </summary>
    ExclusiveFullscreen,
    /// <summary>Screen locked, screensaver, or Windows quiet hours.</summary>
    Away,
}

/// <summary>Decides whether a connection popup would interrupt the user.</summary>
public static class FullscreenDetector
{
    // QUERY_USER_NOTIFICATION_STATE
    private const int QunsNotPresent = 1;
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;
    private const int QunsQuietTime = 6;

    /// <summary>Borderless games often overshoot or undershoot the monitor by a few pixels.</summary>
    private const int CoverTolerance = 2;

    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    public static InterruptionState GetState()
    {
        if (NativeMethods.SHQueryUserNotificationState(out int state) == 0)
        {
            switch (state)
            {
                case QunsRunningD3DFullScreen:
                case QunsPresentationMode:
                    return InterruptionState.ExclusiveFullscreen;
                case QunsBusy:
                    return InterruptionState.Fullscreen;
                case QunsNotPresent:
                case QunsQuietTime:
                    return InterruptionState.Away;
            }
        }

        // Borderless "fullscreen" games often report ACCEPTS_NOTIFICATIONS, so also compare the
        // foreground window with the bounds of its monitor.
        return ForegroundCoversMonitor() ? InterruptionState.Fullscreen : InterruptionState.Available;
    }

    /// <summary>True when <paramref name="window"/> covers <paramref name="monitor"/> (within a few pixels).</summary>
    public static bool Covers(PixelRect window, PixelRect monitor, int tolerance = CoverTolerance) =>
        window.X <= monitor.X + tolerance
        && window.Y <= monitor.Y + tolerance
        && window.Right >= monitor.Right - tolerance
        && window.Bottom >= monitor.Bottom - tolerance;

    private static bool ForegroundCoversMonitor()
    {
        nint hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0 || hwnd == NativeMethods.GetShellWindow()) return false;

        var buffer = new char[64];
        int length = NativeMethods.GetClassName(hwnd, buffer, buffer.Length);
        string className = new(buffer, 0, Math.Max(0, length));
        if (ShellClasses.Contains(className, StringComparer.Ordinal)) return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
        var monitor = ShellEnvironment.GetMonitorForWindow(hwnd);
        return Covers(PixelRect.FromLtrb(rect.Left, rect.Top, rect.Right, rect.Bottom), monitor.Bounds);
    }
}
