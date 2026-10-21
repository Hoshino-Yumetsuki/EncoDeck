// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.Core.Configuration;
using EncoDeck.Platform.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace EncoDeck.App.Controls;

/// <summary>
/// Records the system-wide hotkey of one <see cref="Core.Configuration.HotkeyAction"/>: click, then press
/// the combination. Esc cancels, Backspace or Delete removes it. While recording, the registered
/// hotkeys are released so that pressing one of them is recorded rather than run.
/// </summary>
public sealed partial class HotkeyBox : UserControl
{
    public static readonly DependencyProperty ActionNameProperty = DependencyProperty.Register(
        nameof(ActionName), typeof(string), typeof(HotkeyBox), new PropertyMetadata(null, (d, _) => ((HotkeyBox)d).Refresh()));

    private bool _recording;
    private bool _swallowKeyUp;
    private HotkeyService? _service;

    public HotkeyBox()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _service = App.Instance.Hotkeys;
            if (_service is not null) _service.StatusChanged += OnStatusChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            StopRecording();
            if (_service is not null) _service.StatusChanged -= OnStatusChanged;
            _service = null;
        };
    }

    /// <summary>Name of the <see cref="Core.Configuration.HotkeyAction"/> this box edits.</summary>
    public string? ActionName
    {
        get => (string?)GetValue(ActionNameProperty);
        set => SetValue(ActionNameProperty, value);
    }

    private HotkeyAction? Action => Enum.TryParse<HotkeyAction>(ActionName, out var action) ? action : null;

    private void OnStatusChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_recording || Action is not { } action) return;
        string? text = App.Instance.Settings.GetHotkey(action);
        bool set = Hotkey.TryParse(text, out var hotkey);
        KeyText.Text = set ? hotkey.Format(" + ") : Loc.Get("Hotkey_None");
        ClearButton.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
        var status = set ? _service?.StatusOf(action) : null;
        ShowStatus(status switch
        {
            HotkeyRegistration.InUse => Loc.Get("Hotkey_InUse"),
            HotkeyRegistration.Invalid => Loc.Get("Hotkey_Invalid"),
            _ => null,
        });
    }

    private void ShowStatus(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording();
            return;
        }
        _recording = true;
        _service?.Suspend();
        KeyText.Text = Loc.Get("Hotkey_Recording");
        ShowStatus(null);
        RecordButton.Focus(FocusState.Programmatic);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        StopRecording();
        if (Action is { } action) App.Instance.Settings.SetHotkey(action, null);
        Refresh();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e) => StopRecording();

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        int key = (int)e.Key;
        var modifiers = CurrentModifiers();
        if (Hotkey.IsModifierKey(key))
        {
            // Show what is held so far.
            var held = modifiers | Hotkey.ModifierOf(key);
            KeyText.Text = held == HotkeyModifiers.None ? Loc.Get("Hotkey_Recording") : Hotkey.FormatModifiers(held, " + ") + " + …";
            return;
        }
        if (modifiers == HotkeyModifiers.None && e.Key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }
        if (modifiers == HotkeyModifiers.None && e.Key is VirtualKey.Back or VirtualKey.Delete)
        {
            StopRecording();
            if (Action is { } cleared) App.Instance.Settings.SetHotkey(cleared, null);
            Refresh();
            return;
        }
        var hotkey = new Hotkey(modifiers, key);
        if (!hotkey.IsValid)
        {
            ShowStatus(Loc.Get("Hotkey_NeedsModifier"));
            return;
        }
        StopRecording();
        if (Action is { } action) App.Instance.Settings.SetHotkey(action, hotkey);
        Refresh();
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        // The key that ended the recording must not also press the button.
        if (!_recording && !_swallowKeyUp) return;
        e.Handled = true;
        if (!_recording) _swallowKeyUp = false;
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        _swallowKeyUp = true;
        _service?.Resume();
        Refresh();
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) modifiers |= HotkeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) modifiers |= HotkeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= HotkeyModifiers.Windows;
        return modifiers;

        static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }
}
