// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Interop;

namespace EncoDeck.Platform.Shell;

/// <summary>Monitor geometry for one display.</summary>
public sealed record MonitorLayout(PixelRect Bounds, PixelRect WorkArea, double Scale, TaskbarEdge? TaskbarEdge);

/// <summary>Read-only queries about the Windows shell. All calls are cheap and synchronous.</summary>
public static class ShellEnvironment
{
    public static PixelPoint GetCursorPosition() =>
        NativeMethods.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : new PixelPoint(0, 0);

    /// <summary>Layout of the monitor containing <paramref name="point"/> (nearest monitor if none).</summary>
    public static MonitorLayout GetMonitorAt(PixelPoint point)
    {
        nint monitor = NativeMethods.MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return Describe(monitor);
    }

    public static MonitorLayout GetMonitorForWindow(nint hwnd) =>
        Describe(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));

    /// <summary>Monitor that hosts the primary taskbar (where the notification area lives).</summary>
    public static MonitorLayout GetPrimaryTaskbarMonitor()
    {
        var data = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data) != 0)
        {
            var center = new PixelPoint((data.rc.Left + data.rc.Right) / 2, (data.rc.Top + data.rc.Bottom) / 2);
            var layout = GetMonitorAt(center);
            TaskbarEdge edge = data.uEdge switch
            {
                0 => TaskbarEdge.Left,
                1 => TaskbarEdge.Top,
                2 => TaskbarEdge.Right,
                _ => TaskbarEdge.Bottom,
            };
            return layout with { TaskbarEdge = edge };
        }
        return GetMonitorAt(new PixelPoint(0, 0));
    }

    /// <summary>True when the user asked Windows to reduce animations ("Animation effects" off).</summary>
    public static bool AnimationsEnabled() =>
        !NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETCLIENTAREAANIMATION, 0, out int enabled, 0) || enabled != 0;

    private static MonitorLayout Describe(nint monitor)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var fallback = new PixelRect(0, 0, 1920, 1080);
            return new MonitorLayout(fallback, fallback, 1.0, TaskbarEdge.Bottom);
        }
        var bounds = PixelRect.FromLtrb(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
        var work = PixelRect.FromLtrb(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        double scale = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        return new MonitorLayout(bounds, work, scale, PopupPlacement.InferEdge(bounds, work));
    }
}
