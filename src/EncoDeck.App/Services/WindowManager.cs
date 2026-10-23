// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Views;
using EncoDeck.Platform.Settings;
using EncoDeck.Platform.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace EncoDeck.App.Services;

/// <summary>
/// Creates windows lazily and guarantees at most one of each. Closing the main window releases
/// it entirely (the app keeps running in the tray when the user wants that).
/// The main window's size and position are remembered in <paramref name="settings"/>, when given.
/// </summary>
public sealed class WindowManager(ThemeService theme, Func<bool> keepRunningInTray, Action exit, ISettingsStore? settings = null)
{
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);
    /// <summary>How long nothing may be on screen before the UI's memory is given back.</summary>
    private static readonly TimeSpan TrimDelay = TimeSpan.FromSeconds(3);

    private MainWindow? _main;
    private TrayPanelWindow? _panel;
    private ConnectionPopupWindow? _popup;
    private QuickSettingsPanelWindow? _quickSettings;
    private DispatcherQueueTimer? _trimTimer;

    public bool IsMainVisible => _main is not null;

    internal ThemeService Theme => theme;

    /// <summary>Window handle for pickers and dialogs (0 when the main window is closed).</summary>
    public nint MainHandle => _main is null ? 0 : WindowInterop.GetHandle(_main);

    public void ShowMain(string? page = null)
    {
        _panel?.HidePanel();
        bool created = _main is null;
        if (_main is null)
        {
            var main = new MainWindow();
            _main = main;
            theme.Register(main);
            if (settings?.Current.MainWindowBounds is { } saved) main.RestoreBounds(saved);
            main.Closed += (_, _) =>
            {
                if (settings is not null && main.SavedBounds is { } bounds) settings.Update(s => s with { MainWindowBounds = bounds });
                _main = null;
                if (!keepRunningInTray()) exit();
                else TrimWhenIdle();
            };
        }
        if (page is not null) _main.Navigate(page);

        var appWindow = WindowInterop.GetAppWindow(_main);
        if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        appWindow.Show(activateWindow: true);
        _main.Activate();
        if (created) _main.ApplyMaximized();
        WindowInterop.BringToForeground(_main);
    }

    public void ToggleTrayPanel(Guid trayIconId)
    {
        if (_panel is { IsShown: true })
        {
            _panel.HidePanel();
            return;
        }
        // Clicking the tray icon while the panel is open first deactivates (hides) it; don't reopen.
        if (_panel is not null && DateTimeOffset.UtcNow - _panel.LastHiddenAt < ReopenGuard) return;

        if (_panel is null)
        {
            _panel = new TrayPanelWindow();
            theme.Register(_panel);
            _panel.Closed += (_, _) => _panel = null;
            _panel.Hidden += (_, _) => TrimWhenIdle();
        }
        _panel.ShowNear(WindowInterop.GetTrayIconRect(trayIconId));
    }

    /// <param name="duration">Display time when the media has no official HeyMelody timeline.</param>
    /// <param name="clickThrough">A fullscreen app or game is in front: never take mouse input.</param>
    /// <param name="placement">Explicit bounds in physical pixels (diagnostics).</param>
    public ConnectionPopupWindow? ShowConnectionPopup(TimeSpan duration, bool clickThrough = false, PixelRect? placement = null)
    {
        if (_popup is not null) return null; // one popup at a time
        var popup = new ConnectionPopupWindow(duration, clickThrough);
        _popup = popup;
        theme.Register(popup);
        popup.Dismissed += (_, _) => { if (_popup == popup) _popup = null; TrimWhenIdle(); };
        popup.Closed += (_, _) => { if (_popup == popup) _popup = null; };
        popup.ShowPopup(placement);
        return popup;
    }

    /// <summary>Shows the device panel on top of Windows 11 Quick Settings' cards (physical pixels), or moves it there.</summary>
    public void TrackQuickSettings(PixelRect cards)
    {
        if (_quickSettings is null)
        {
            _quickSettings = new QuickSettingsPanelWindow();
            theme.Register(_quickSettings, static s => s.QuickSettingsPanelTheme);
            _quickSettings.Closed += (_, _) => _quickSettings = null;
            _quickSettings.Hidden += (_, _) => TrimWhenIdle();
        }
        _quickSettings.Track(cards);
    }

    /// <summary>Quick Settings is closing: the panel on top of it goes too.</summary>
    public void LeaveQuickSettings() => _quickSettings?.Leave();

    /// <summary>
    /// While only the tray icon is left, the memory the windows used is given back: a few seconds
    /// after the last window closes or hides (and after a launch into the tray) the managed heap is
    /// compacted and the working set trimmed. The pages come back by themselves when a window opens.
    /// </summary>
    public void TrimWhenIdle()
    {
        if (_trimTimer is null)
        {
            _trimTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _trimTimer.Interval = TrimDelay;
            _trimTimer.IsRepeating = false;
            _trimTimer.Tick += (_, _) =>
            {
                if (!HasWindowOnScreen) ProcessMemory.Trim();
            };
        }
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    private bool HasWindowOnScreen => _main is not null || _panel is { IsShown: true } || _popup is not null || _quickSettings is { IsShown: true };

    public void CloseAll()
    {
        _popup?.Close();
        _quickSettings?.Close();
        _panel?.Close();
        var main = _main;
        _main = null;
        main?.Close();
    }
}
