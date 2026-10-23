// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Text.Json;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Configuration;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EncoDeck.App.Views;
using EncoDeck.Platform.Shell;

namespace EncoDeck.App.Services;

/// <summary>Explicitly launched, persistent manual previews; the Bluetooth runtime is never started.</summary>
internal sealed class BrandDemo
{
    internal sealed record Entry(string Brand, string Name, string Directory, string? ProfileId = null,
        string? Popup = null, string? PopupDark = null, string? Model = null, string? Motion = null, string? MotionDark = null);
    private readonly App _app;
    private readonly Entry[] _entries;
    private readonly Window _picker = new() { Title = "EncoDeck · 多品牌手动演示" };
    private readonly ComboBox _brands = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TrayIconService _tray;
    private Entry? _current;
    private bool _exiting;

    public BrandDemo(App app, string manifest, DispatcherQueue dispatcher)
    {
        _app = app;
        _entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(manifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("No demo models.");
        if (_entries.Length == 0) throw new InvalidDataException("No demo models.");
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest))!, "session");
        app.SetHostForDiagnostics(AppHost.Create(rootOverride: root));
        app.CreateViewModelsForDiagnostics(dispatcher, new DemoCommands(app));
        app.Windows = ForDemoWindows(app);
        app.Device.ArtworkPinned = true;
        _tray = new TrayIconService(app.Device, app.Host.Settings, app.Windows, Exit);
        _tray.Create();

        var content = new StackPanel { Padding = new Thickness(24), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = "多品牌耳机 · 手动演示", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "选择品牌后查看实际 WinUI 页面。电量与连接状态为演示数据，素材来自对应型号；本模式不连接蓝牙。", TextWrapping = TextWrapping.Wrap });
        foreach (var entry in _entries) _brands.Items.Add($"{entry.Brand} · {entry.Name}");
        _brands.SelectionChanged += async (_, _) =>
        {
            if (_brands.SelectedIndex < 0) return;
            await SelectAsync(_entries[_brands.SelectedIndex]);
        };
        content.Children.Add(_brands);
        Add("打开该型号首页", () => app.Windows.ShowMain("Home"));
        Add("重播连接／开盖弹窗", () => app.Windows.ShowConnectionPopup(TimeSpan.FromSeconds(12)));
        Add("打开托盘小窗口（也可单击系统托盘图标）", () => app.Windows.ToggleTrayPanel(_tray.Id));
        var theme = new ComboBox { Header = "预览主题", HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.Items.Add("跟随系统"); theme.Items.Add("浅色"); theme.Items.Add("深色");
        theme.SelectedIndex = 0;
        theme.SelectionChanged += (_, _) => app.Host.Settings.Update(s => s with { Theme = theme.SelectedIndex switch { 1 => AppTheme.Light, 2 => AppTheme.Dark, _ => AppTheme.System } });
        content.Children.Add(theme);
        content.Children.Add(_status);
        Add("退出全部演示窗口", Exit);
        _picker.Content = new ScrollViewer { Content = content };
        WindowInterop.GetAppWindow(_picker).Resize(new Windows.Graphics.SizeInt32(560, 690));
        _picker.Closed += (_, _) => Exit();
        void Add(string text, Action action)
        {
            var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => action(); content.Children.Add(button);
        }
    }

    private WindowManager ForDemoWindows(App app) => new(new ThemeService(app.Host.Settings), static () => true, Exit);

    public async void Show(bool checkAll = false, string? initialBrand = null)
    {
        if (checkAll) await CheckAllAsync();
        _brands.SelectedIndex = Math.Max(0, Array.FindIndex(_entries, e => string.Equals(e.Brand, initialBrand, StringComparison.OrdinalIgnoreCase)));
        _picker.Activate();
    }

    private async Task SelectAsync(Entry entry, bool show = true)
    {
        _current = entry;
        _app.Windows.CloseAll();
        var device = new DeviceIdentity("brand-demo-" + entry.Brand, entry.Name, new BluetoothAddress(0));
        var profiles = BuiltInProfiles.Create();
        var profile = entry.ProfileId is { } id ? profiles.Single(p => p.ModelId == id)
            : profiles.OrderByDescending(p => p.MatchScore(device)).First(p => p.MatchScore(device) > 0);
        // Demo state simulates a successful parameter query; no Bluetooth session runs.
        if (profile is EncoDeck.Protocol.Brands.Sony.SonyAkgProfile sonyAkg)
            profile = sonyAkg.WithNoiseSupport(true);
        var now = DateTimeOffset.UtcNow;
        Observed<T> O<T>(T value) => Observed.From(value, now, StateSource.QueryResponse);
        bool Has(DeviceCapability cap) => profile.Capabilities.IsAvailable(cap, false);
        var toggles = profile is IControllableProfile c ? c.Toggles : [];
        var noise = profile.NoiseControlOptions.FirstOrDefault(o => o.Mode == NoiseControlMode.NoiseCancellation && o.Support == CapabilitySupport.Supported);
        var eq = profile.EqualizerPresets.FirstOrDefault(p => p.Support == CapabilitySupport.Supported);
        _app.Host.Store.Reset(DeviceState.Empty with
        {
            Device = device, Profile = profile, Connection = DeviceConnectionState.ControlReady,
            Battery = new BatteryStatus(O(new BatteryReading(85, false)), profile.Form == DeviceForm.Headphones ? null : O(new BatteryReading(80, false)),
                profile.Form == DeviceForm.Headphones ? null : O(new BatteryReading(65, false))),
            NoiseControl = Has(DeviceCapability.NoiseCancellation) && noise is not null ? O(new NoiseControlState(noise.Mode, noise.LevelId)) : null,
            Equalizer = Has(DeviceCapability.Equalizer) && eq is not null ? O(new EqualizerState(eq.Id, 0)) : null,
            Toggles = toggles.ToImmutableDictionary(t => t, t => O(t == DeviceToggle.DualDeviceConnection)),
            Spatial = Has(DeviceCapability.SpatialAudio) ? O(SpatialAudioMode.Off) : null,
        });
        await Task.Delay(150);
        if (_current != entry || _exiting) return;
        ApplyArtwork(entry);
        _status.Text = $"当前：{entry.Name}\n首页：{(entry.Model is null ? entry.Motion is null ? "通用产品悬浮动效（非原厂开盖）" : entry.Motion.EndsWith(".device-once.mp4", StringComparison.OrdinalIgnoreCase) ? "官方首页动画（播放一次，保留末帧）" : "官方循环产品动画" : Path.GetExtension(entry.Model) == ".vfxms" ? "官方 3D 场景，可拖动旋转" : "官方动态转盘，可拖动旋转")}；弹窗："
            + (Path.GetExtension(entry.Popup) == ".json" ? "官方 Lottie 动画" : Path.GetExtension(entry.Popup) == ".webp" ? "官方逐帧开盖动画" : IsVideo(entry.Popup) ? "官方连接视频" : "通用产品入场动效（非原厂开盖）")
            + "。可重复打开弹窗；托盘图标提示当前型号。基础降噪、EQ 与开关操作只修改演示状态。";
        if (show)
        {
            _app.Windows.ShowMain("Home");
            _picker.Activate();
        }
    }

    private void ApplyArtwork(Entry entry)
    {
        UiSnapshot.ApplyArtworkDirectory(_app, entry.Directory);
        _app.Device.DeviceName = entry.Name;
        _app.Device.DeviceModelPath = entry.Model;
        _app.Device.DeviceMotionIsVideo = IsVideo(entry.Motion);
        _app.Device.DeviceMotionPath = entry.Motion;
        _app.Device.DeviceMotionDarkPath = entry.MotionDark;
        if (entry.Model is not null || entry.Motion is not null) _app.Device.HasComponentArtwork = false;
        _app.Device.PopupMediaPath = entry.Popup ?? _app.Device.DeviceImagePath;
        _app.Device.PopupMediaDarkPath = entry.PopupDark ?? _app.Device.PopupMediaPath;
        _app.Device.PopupMediaIsVideo = IsVideo(entry.Popup);
        _app.Device.PopupMediaDarkIsVideo = IsVideo(entry.PopupDark ?? entry.Popup);
    }

    private static bool IsVideo(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() is ".mp4" or ".webm";

    private async Task CheckAllAsync()
    {
        string output = Path.Combine(_app.Host.Paths.Root, "previews");
        Directory.CreateDirectory(output);
        var results = new List<object>();
        foreach (var entry in _entries)
        {
            try
            {
                await SelectAsync(entry, show: false);
                var main = new MainWindow();
                double scale = WindowInterop.GetScale(main);
                WindowInterop.GetAppWindow(main).MoveAndResize(new Windows.Graphics.RectInt32(-24000, -24000, (int)(1160 * scale), (int)(760 * scale)));
                WindowInterop.GetAppWindow(main).Show(activateWindow: false);
                main.Navigate("Home");
                await Task.Delay(900);
                string[] mediaBefore = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceMedia>().Select(m => m.DiagnosticState).ToArray();
                string[] modelBefore = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceModelView>().Select(m => m.DiagnosticState).ToArray();
                await UiSnapshot.CaptureAsync(main, Path.Combine(output, entry.Brand + "-home.png"));
                await Task.Delay(1300);
                string[] modelAfter = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceModelView>().Select(m => m.DiagnosticState).ToArray();
                string[] homeMedia = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceMedia>().Select(m => m.DiagnosticState).ToArray();
                await UiSnapshot.CaptureAsync(main, Path.Combine(output, entry.Brand + "-home-motion.png"));
                string[] themeMedia = [];
                if (entry.MotionDark is not null && main.Content is FrameworkElement themed)
                {
                    themed.RequestedTheme = ElementTheme.Light;
                    await Task.Delay(700);
                    await UiSnapshot.CaptureAsync(main, Path.Combine(output, entry.Brand + "-home-light.png"));
                    themed.RequestedTheme = ElementTheme.Dark;
                    await Task.Delay(700);
                    await UiSnapshot.CaptureAsync(main, Path.Combine(output, entry.Brand + "-home-dark.png"));
                    themeMedia = UiSnapshot.Descendants(main.Content).OfType<Controls.DeviceMedia>().Select(m => m.DiagnosticState).ToArray();
                }
                main.Close();
                var popup = _app.Windows.ShowConnectionPopup(TimeSpan.FromSeconds(8), placement: new PixelRect(-24000, -24000, 500, 500));
                await Task.Delay(1500);
                if (popup is null) throw new InvalidOperationException("Preview popup was not created.");
                await UiSnapshot.CaptureAsync(popup, Path.Combine(output, entry.Brand + "-popup.png"));
                string state = popup.DiagnosticState;
                _app.Windows.CloseAll();
                var panel = new TrayPanelWindow();
                _app.Windows.Theme.Register(panel);
                panel.ShowForDiagnostics(new PixelPoint(-24000, -24000), scale);
                await Task.Delay(400);
                await UiSnapshot.CaptureAsync(panel, Path.Combine(output, entry.Brand + "-tray.png"));
                panel.Close();
                results.Add(new { entry.Brand, entry.Name, Profile = _app.Host.Store.Current.Profile?.ModelId, Ok = true, ModelBefore = modelBefore, ModelAfter = modelAfter, HomeMediaBefore = mediaBefore, HomeMedia = homeMedia, ThemeMedia = themeMedia, Popup = state });
            }
            catch (Exception ex)
            {
                results.Add(new { entry.Brand, Ok = false, Error = ex.ToString() });
                _app.Windows.CloseAll();
            }
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _tray.Dispose(); _app.Windows.CloseAll(); _picker.Close();
        _app.Host.Loggers.Dispose(); _app.Host.FileLog.Dispose(); _app.Exit();
    }

    private sealed class DemoCommands(App app) : IDeviceCommandService
    {
        private Task<CommandResult> Apply(Func<DeviceState, DeviceState> update)
        {
            app.Host.Store.Reset(update(app.Host.Store.Current));
            return Task.FromResult(CommandResult.Ok);
        }
        private static Observed<T> O<T>(T value) => Observed.From(value, DateTimeOffset.UtcNow, StateSource.QueryResponse);
        public Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken = default) => Apply(s => s with { NoiseControl = O(target) });
        public Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken = default) => Apply(s => s with { Equalizer = O(new EqualizerState(presetId, 0)) });
        public Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken = default) => Apply(s => s with { Toggles = s.Toggles.SetItem(toggle, O(enabled)) });
        public Task<CommandResult> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(CommandResult.Ok);
        public Task<CommandResult> ReconnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(CommandResult.Ok);
    }
}
