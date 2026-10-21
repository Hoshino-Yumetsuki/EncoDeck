// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using EncoDeck.Core.Configuration;
using EncoDeck.Platform.Interop;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Platform.Input;

/// <summary>Why a hotkey could not be registered.</summary>
public enum HotkeyRegistration
{
    Registered,
    /// <summary>Another app (or Windows itself) already owns the combination.</summary>
    InUse,
    Invalid,
}

/// <summary>
/// System-wide hotkeys (<c>RegisterHotKey</c>) delivered to a message-only window. Create it on a
/// thread that pumps messages (the UI thread); <see cref="Pressed"/> is raised there. Key repeat is
/// off: holding the keys fires once.
/// </summary>
public sealed unsafe class GlobalHotkeys : IDisposable
{
    private const string WindowClass = "EncoDeck.Hotkeys";
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    /// <summary>The window procedure is static (unmanaged); one instance per process.</summary>
    private static GlobalHotkeys? s_active;

    private readonly ILogger _logger;
    private readonly HashSet<int> _ids = [];
    private nint _window;

    public GlobalHotkeys(ILogger<GlobalHotkeys> logger)
    {
        _logger = logger;
        if (s_active is not null) throw new InvalidOperationException("Global hotkeys are already in use.");
        nint instance = NativeMethods.GetModuleHandle(null);
        fixed (char* name = WindowClass)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = instance,
                lpszClassName = name,
            };
            // Zero when the class exists already (an earlier instance in this process): fine.
            NativeMethods.RegisterClassEx(windowClass);
        }
        _window = NativeMethods.CreateWindowEx(0, WindowClass, string.Empty, 0, 0, 0, 0, 0, NativeMethods.HWND_MESSAGE, 0, instance, 0);
        if (_window == 0) _logger.LogWarning("Hotkey window not created (error {Error})", Marshal.GetLastPInvokeError());
        s_active = this;
    }

    /// <summary>The id passed to <see cref="Register"/>.</summary>
    public event EventHandler<int>? Pressed;

    /// <summary>The message-only window (tests).</summary>
    internal nint WindowHandle => _window;

    public HotkeyRegistration Register(int id, Hotkey hotkey)
    {
        Unregister(id);
        if (!hotkey.IsValid || _window == 0) return HotkeyRegistration.Invalid;
        if (!NativeMethods.RegisterHotKey(_window, id, (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)hotkey.Key))
        {
            int error = Marshal.GetLastPInvokeError();
            _logger.LogInformation("Hotkey {Hotkey} not registered (error {Error})", hotkey, error);
            return error == ERROR_HOTKEY_ALREADY_REGISTERED ? HotkeyRegistration.InUse : HotkeyRegistration.Invalid;
        }
        _ids.Add(id);
        return HotkeyRegistration.Registered;
    }

    public void Unregister(int id)
    {
        if (_ids.Remove(id)) NativeMethods.UnregisterHotKey(_window, id);
    }

    public void UnregisterAll()
    {
        foreach (int id in _ids) NativeMethods.UnregisterHotKey(_window, id);
        _ids.Clear();
    }

    public void Dispose()
    {
        if (_window == 0 && s_active != this) return;
        UnregisterAll();
        if (_window != 0) NativeMethods.DestroyWindow(_window);
        _window = 0;
        if (s_active == this) s_active = null;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == NativeMethods.WM_HOTKEY && s_active is { } active && hwnd == active._window)
        {
            // An exception must not unwind into user32.
            try
            {
                active.Pressed?.Invoke(active, (int)wParam);
            }
            catch (Exception ex)
            {
                active._logger.LogError(ex, "Hotkey {Id} handler failed", (int)wParam);
            }
            return 0;
        }
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
