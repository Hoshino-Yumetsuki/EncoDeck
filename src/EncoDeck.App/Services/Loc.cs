// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace EncoDeck.App.Services;

/// <summary>Localized strings for code (XAML uses x:Uid). Falls back to the key when a string is missing.</summary>
internal static class Loc
{
    private static ResourceLoader? _loader;

    public static string Get(string key)
    {
        try
        {
            _loader ??= new ResourceLoader();
            string value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
