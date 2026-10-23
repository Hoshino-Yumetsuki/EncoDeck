// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.ViewModels;
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Platform.Settings;
using EncoDeck.Platform.Shell;
using Microsoft.UI.Dispatching;

namespace EncoDeck.App.Services;

/// <summary>
/// Puts the device panel on top of Windows 11 Quick Settings while it is open, when the user has
/// that switched on and earbuds are paired (and, unless the user opted out, Bluetooth is on).
/// The shell hooks only run while the setting is on.
/// </summary>
public sealed class QuickSettingsCompanion : IDisposable
{
    /// <summary>
    /// How often the cards are re-read while Quick Settings is open: its sub-pages and the media
    /// card change their height. Reading them is two cheap Win32 calls.
    /// </summary>
    private static readonly TimeSpan FollowInterval = TimeSpan.FromMilliseconds(50);

    private readonly ISettingsStore _settings;
    private readonly WindowManager _windows;
    private readonly DeviceViewModel _device;
    private readonly QuickSettingsWatcher _watcher;
    private readonly BluetoothRadioMonitor _radio;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;

    public QuickSettingsCompanion(ISettingsStore settings, WindowManager windows, DeviceViewModel device, QuickSettingsWatcher watcher, BluetoothRadioMonitor radio, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _windows = windows;
        _device = device;
        _watcher = watcher;
        _radio = radio;
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = FollowInterval;
        _timer.Tick += (_, _) => Follow();
        _watcher.Opened += (_, _) =>
        {
            _timer.Start();
            Follow();
        };
        _watcher.Closing += (_, _) => Leave();
        _settings.Changed += OnSettingsChanged;
        Apply();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _timer.Stop();
        _watcher.Dispose();
        _radio.Dispose();
    }

    private void OnSettingsChanged(object? sender, Core.Configuration.AppSettings e) => _dispatcher.TryEnqueue(Apply);

    private void Apply()
    {
        bool enabled = _settings.Current.ShowAboveQuickSettings != false && QuickSettingsWatcher.IsSupported;
        if (enabled == _watcher.IsRunning) return;
        if (enabled)
        {
            _watcher.Start();
        }
        else
        {
            _watcher.Stop();
            Leave();
        }
    }

    private void Follow()
    {
        if (!_watcher.IsOpen)
        {
            Leave();
            return;
        }
        bool hiddenForRadio = _radio.IsOff && _settings.Current.HideQuickSettingsPanelWhenBluetoothOff != false;
        if (_device.HasDevice && !hiddenForRadio && _watcher.GetCards() is { } cards) _windows.TrackQuickSettings(cards);
        else _windows.LeaveQuickSettings();
    }

    private void Leave()
    {
        _timer.Stop();
        _windows.LeaveQuickSettings();
    }
}
