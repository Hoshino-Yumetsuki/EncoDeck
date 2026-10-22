// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using EncoDeck.Platform.Interop;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Platform.Shell;

/// <summary>
/// Follows Windows 11 Quick Settings (Win+A, the Wi-Fi / volume flyout). Windows has no API for it,
/// so this watches its window: ShellHost's <c>ControlCenterWindow</c>. That window is as tall as
/// the work area and transparent; its window region is the part with cards (Quick Settings, and
/// the media card while something plays), which is also the only part that takes clicks.
/// <para>
/// Out-of-context WinEvent hooks: events arrive on the thread that called <see cref="Start"/>,
/// which must pump messages (the UI thread). Nothing is injected into the shell.
/// </para>
/// </summary>
public sealed class QuickSettingsWatcher(ILogger<QuickSettingsWatcher> logger) : IDisposable
{
    private const string WindowClass = "ControlCenterWindow";

    /// <summary>The hook callback is static (unmanaged); only one watcher runs at a time.</summary>
    private static QuickSettingsWatcher? s_active;

    private readonly ILogger _logger = logger;
    private readonly List<nint> _hooks = [];
    private nint _window;
    private bool _open;
    private bool _leaving;

    /// <summary>
    /// Windows 11 24H2 (build 26100) or later. Earlier builds show Quick Settings as a
    /// ShellExperienceHost CoreWindow that can only be told apart by its localized title: unsupported.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);

    /// <summary>Quick Settings appeared, or the user went back to it while it was closing.</summary>
    public event EventHandler? Opened;

    /// <summary>
    /// Quick Settings is going away. Raised when it loses the foreground, which is when its own
    /// close animation starts (it hides about 300 ms later), or when it hides without that.
    /// </summary>
    public event EventHandler? Closing;

    public bool IsRunning => _hooks.Count > 0;

    /// <summary>Quick Settings is on screen and not closing.</summary>
    public bool IsOpen => _open && !_leaving;

    public unsafe void Start()
    {
        if (IsRunning || !IsSupported) return;
        if (s_active is not null && s_active != this) throw new InvalidOperationException("Another Quick Settings watcher is running.");
        s_active = this;
        Hook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
        Hook(NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.EVENT_OBJECT_HIDE);
        Hook(NativeMethods.EVENT_OBJECT_CLOAKED, NativeMethods.EVENT_OBJECT_UNCLOAKED);
        // Already open when the feature is switched on.
        if (FindOpenWindow() is { } open) Handle(NativeMethods.EVENT_OBJECT_SHOW, open);
    }

    public void Stop()
    {
        foreach (nint hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        if (s_active == this) s_active = null;
        _window = 0;
        _open = false;
        _leaving = false;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Screen bounds (physical pixels) of the Quick Settings cards including the shell's margin
    /// around them, or null when Quick Settings is closed or its layout cannot be read.
    /// </summary>
    public PixelRect? GetCards()
    {
        if (!IsOpen || !NativeMethods.GetWindowRect(_window, out var window)) return null;
        if (NativeMethods.GetWindowRgnBox(_window, out var box) <= NativeMethods.NULLREGION) return null;
        var cards = PixelRect.FromLtrb(window.Left + box.Left, window.Top + box.Top, window.Left + box.Right, window.Top + box.Bottom);
        if (cards.Width <= 0 || cards.Height <= 0) return null;
        int top = Math.Min(cards.Y, TopOfOtherShellCards(cards));
        return PixelRect.FromLtrb(cards.X, top, cards.Right, cards.Bottom);
    }

    private unsafe void Hook(uint first, uint last)
    {
        nint hook = NativeMethods.SetWinEventHook(first, last, 0, &OnWinEvent, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (hook == 0) _logger.LogWarning("SetWinEventHook {First:X}-{Last:X} failed", first, last);
        else _hooks.Add(hook);
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint winEvent, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0 || hwnd == 0) return;
        var watcher = s_active;
        if (watcher is null) return;
        // An exception must not unwind into user32.
        try
        {
            watcher.Handle(winEvent, hwnd);
        }
        catch (Exception ex)
        {
            watcher._logger.LogError(ex, "Quick Settings event {Event:X} failed", winEvent);
        }
    }

    private void Handle(uint winEvent, nint hwnd)
    {
        switch (winEvent)
        {
            case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                // Own windows are skipped by the hook: clicking EncoDeck's panel is not leaving.
                if (!_open) return;
                if (hwnd == _window)
                {
                    if (!_leaving) return;
                    _leaving = false;
                    Opened?.Invoke(this, EventArgs.Empty);
                }
                else if (!_leaving)
                {
                    _leaving = true;
                    Closing?.Invoke(this, EventArgs.Empty);
                }
                return;

            case NativeMethods.EVENT_OBJECT_SHOW or NativeMethods.EVENT_OBJECT_UNCLOAKED:
                if (hwnd != _window && !IsQuickSettings(hwnd)) return;
                if (!IsOnScreen(hwnd)) return;
                _window = hwnd;
                if (IsOpen) return;
                _open = true;
                _leaving = false;
                Opened?.Invoke(this, EventArgs.Empty);
                return;

            case NativeMethods.EVENT_OBJECT_HIDE or NativeMethods.EVENT_OBJECT_CLOAKED or NativeMethods.EVENT_OBJECT_DESTROY:
                if (hwnd != _window || !_open) return;
                bool announced = _leaving;
                _open = false;
                _leaving = false;
                if (winEvent == NativeMethods.EVENT_OBJECT_DESTROY) _window = 0;
                if (!announced) Closing?.Invoke(this, EventArgs.Empty);
                return;
        }
    }

    private static bool IsQuickSettings(nint hwnd)
    {
        var buffer = new char[WindowClass.Length + 2];
        int length = NativeMethods.GetClassName(hwnd, buffer, buffer.Length);
        return length == WindowClass.Length && buffer.AsSpan(0, length).SequenceEqual(WindowClass);
    }

    private static bool IsOnScreen(nint hwnd) =>
        NativeMethods.IsWindowVisible(hwnd)
        && (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0);

    private static unsafe nint? FindOpenWindow()
    {
        var search = new WindowSearch();
        var handle = GCHandle.Alloc(search);
        try
        {
            NativeMethods.EnumWindows(&OnFindWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return search.Found;
    }

    [UnmanagedCallersOnly]
    private static int OnFindWindow(nint hwnd, nint state)
    {
        var search = (WindowSearch)GCHandle.FromIntPtr(state).Target!;
        if (!IsQuickSettings(hwnd) || !IsOnScreen(hwnd)) return 1;
        search.Found = hwnd;
        return 0;
    }

    /// <summary>
    /// Some builds may show the media controls as a separate shell window above the cards rather
    /// than inside the region: stay above any other visible window of the Quick Settings process
    /// that sits over the cards. Returns <see cref="int.MaxValue"/> when there is none.
    /// </summary>
    private unsafe int TopOfOtherShellCards(PixelRect cards)
    {
        NativeMethods.GetWindowThreadProcessId(_window, out uint processId);
        var scan = new SiblingScan(_window, processId, cards);
        var handle = GCHandle.Alloc(scan);
        try
        {
            NativeMethods.EnumWindows(&OnSiblingWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return scan.Top;
    }

    [UnmanagedCallersOnly]
    private static int OnSiblingWindow(nint hwnd, nint state)
    {
        var scan = (SiblingScan)GCHandle.FromIntPtr(state).Target!;
        if (hwnd == scan.Window) return 1;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId != scan.ProcessId || !IsOnScreen(hwnd) || !NativeMethods.GetWindowRect(hwnd, out var r)) return 1;
        var cards = scan.Cards;
        int overlap = Math.Min(r.Right, cards.Right) - Math.Max(r.Left, cards.X);
        // Over the cards' column and ending above their middle: full-height shell surfaces never qualify.
        if (r.Bottom > r.Top && overlap >= cards.Width / 2 && r.Bottom <= cards.Y + cards.Height / 2)
        {
            scan.Top = Math.Min(scan.Top, r.Top);
        }
        return 1;
    }

    private sealed class WindowSearch
    {
        public nint? Found;
    }

    private sealed class SiblingScan(nint window, uint processId, PixelRect cards)
    {
        public nint Window { get; } = window;
        public uint ProcessId { get; } = processId;
        public PixelRect Cards { get; } = cards;
        public int Top = int.MaxValue;
    }
}
