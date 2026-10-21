// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;

namespace EncoDeck.Core.Configuration;

/// <summary>Modifier keys of a hotkey; the values are Win32's MOD_* flags.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>What a global hotkey does.</summary>
public enum HotkeyAction
{
    /// <summary>Noise cancelling ↔ transparency (from any other mode: noise cancelling).</summary>
    ToggleNoise,

    /// <summary>The next mode the earbuds offer: noise cancelling, adaptive, transparency, off.</summary>
    CycleNoise,

    NoiseCancellation,
    Adaptive,
    Transparency,
    NoiseOff,
}

/// <summary>
/// A system-wide key combination: modifiers plus one Win32 virtual key. Written as text such as
/// <c>Ctrl+Alt+N</c>. Without a modifier only F1–F24 are allowed, so a plain key can never be taken
/// away from typing.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)
{
    private static readonly (int Key, string Name)[] Names =
    [
        (0x08, "Backspace"), (0x09, "Tab"), (0x0D, "Enter"), (0x13, "Pause"), (0x20, "Space"),
        (0x21, "PageUp"), (0x22, "PageDown"), (0x23, "End"), (0x24, "Home"),
        (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"),
        (0x2C, "PrintScreen"), (0x2D, "Insert"), (0x2E, "Delete"),
        (0x6A, "Num*"), (0x6B, "Num+"), (0x6D, "Num-"), (0x6E, "Num."), (0x6F, "Num/"),
        (0x91, "ScrollLock"),
        (0xBA, ";"), (0xBB, "="), (0xBC, ","), (0xBD, "-"), (0xBE, "."), (0xBF, "/"), (0xC0, "`"),
        (0xDB, "["), (0xDC, "\\"), (0xDD, "]"), (0xDE, "'"),
    ];

    /// <summary>Has a usable main key and either a modifier or a function key.</summary>
    public bool IsValid => Key is > 0 and < 0xFF && !IsModifierKey(Key) && (Modifiers != HotkeyModifiers.None || IsFunctionKey(Key));

    public static bool IsFunctionKey(int key) => key is >= 0x70 and <= 0x87;

    /// <summary>Shift, Ctrl, Alt, Windows (either side), and the lock keys that cannot be a hotkey's main key.</summary>
    public static bool IsModifierKey(int key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5) or 0x14 or 0x90;

    public static HotkeyModifiers ModifierOf(int key) => key switch
    {
        0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Control,
        0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
        0x5B or 0x5C => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None,
    };

    public override string ToString() => Format("+");

    /// <summary>The same text with a separator for display, e.g. <c>Ctrl + Alt + N</c>.</summary>
    public string Format(string separator) =>
        Modifiers == HotkeyModifiers.None ? KeyName(Key) : FormatModifiers(Modifiers, separator) + separator + KeyName(Key);

    /// <summary>Modifiers alone, in the usual order: Ctrl, Alt, Shift, Win.</summary>
    public static string FormatModifiers(HotkeyModifiers modifiers, string separator)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        return string.Join(separator, parts);
    }

    public static string KeyName(int key) => key switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x60 and <= 0x69 => $"Num{key - 0x60}",
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        _ => Array.Find(Names, n => n.Key == key).Name ?? $"0x{key:X2}",
    };

    /// <summary>Parses text written by <see cref="ToString"/> (spaces and case are ignored). Invalid combinations fail.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        // "Num+" and a "+" key: split on '+' only where it separates parts.
        var parts = new List<string>();
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0 && parts.Count > 0) parts[^1] += "+";
            else parts.Add(part);
        }
        var modifiers = HotkeyModifiers.None;
        for (int i = 0; i < parts.Count - 1; i++)
        {
            var modifier = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None) return false;
            modifiers |= modifier;
        }
        if (ParseKey(parts[^1]) is not { } key) return false;
        hotkey = new Hotkey(modifiers, key);
        return hotkey.IsValid;
    }

    private static int? ParseKey(string name)
    {
        if (name.Length == 1 && char.ToUpperInvariant(name[0]) is var c && (c is >= '0' and <= '9' or >= 'A' and <= 'Z')) return c;
        string upper = name.ToUpperInvariant();
        if (upper.Length > 1 && upper[0] == 'F' && int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24) return 0x6F + f;
        if (upper.StartsWith("NUM", StringComparison.Ordinal) && upper.Length == 4 && upper[3] is >= '0' and <= '9') return 0x60 + (upper[3] - '0');
        if (upper.StartsWith("0X", StringComparison.Ordinal) && int.TryParse(upper.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int vk)) return vk;
        foreach (var (key, keyName) in Names)
        {
            if (string.Equals(keyName, name, StringComparison.OrdinalIgnoreCase)) return key;
        }
        return null;
    }
}

/// <summary>Global hotkeys for noise control, as <see cref="Hotkey"/> text; null means not set.</summary>
public sealed record HotkeyBindings
{
    public string? ToggleNoise { get; init; }
    public string? CycleNoise { get; init; }
    public string? NoiseCancellation { get; init; }
    public string? Adaptive { get; init; }
    public string? Transparency { get; init; }
    public string? NoiseOff { get; init; }

    public string? Get(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleNoise => ToggleNoise,
        HotkeyAction.CycleNoise => CycleNoise,
        HotkeyAction.NoiseCancellation => NoiseCancellation,
        HotkeyAction.Adaptive => Adaptive,
        HotkeyAction.Transparency => Transparency,
        HotkeyAction.NoiseOff => NoiseOff,
        _ => null,
    };

    public HotkeyBindings With(HotkeyAction action, string? hotkey) => action switch
    {
        HotkeyAction.ToggleNoise => this with { ToggleNoise = hotkey },
        HotkeyAction.CycleNoise => this with { CycleNoise = hotkey },
        HotkeyAction.NoiseCancellation => this with { NoiseCancellation = hotkey },
        HotkeyAction.Adaptive => this with { Adaptive = hotkey },
        HotkeyAction.Transparency => this with { Transparency = hotkey },
        HotkeyAction.NoiseOff => this with { NoiseOff = hotkey },
        _ => this,
    };

    /// <summary>Canonical text for every binding; unreadable ones are dropped.</summary>
    public HotkeyBindings Normalize()
    {
        var result = this;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            string? text = Get(action);
            string? canonical = Hotkey.TryParse(text, out var hotkey) ? hotkey.ToString() : null;
            if (canonical != text) result = result.With(action, canonical);
        }
        return result;
    }
}
