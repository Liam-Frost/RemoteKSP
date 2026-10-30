using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace RemoteKSP.Shared.Localization;

/// <summary>A language the UI can be shown in.</summary>
public sealed record UiLanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// UI language preference, shared by the server and client GUIs and kept per user in
/// HKCU\Software\RemoteKSP. Only the UI culture is changed; dates and numbers keep the
/// Windows regional format. A change takes effect the next time the app starts.
/// </summary>
public static class UiLanguage
{
    public const string English = "en";
    public const string ChineseSimplified = "zh-Hans";
    public const string Default = English;

    private const string KeyPath = @"Software\RemoteKSP";
    private const string ValueName = "UILanguage";

    /// <summary>Display names are written in their own language so anyone can find theirs.</summary>
    public static IReadOnlyList<UiLanguageOption> Supported { get; } =
    [
        new(English, "English"),
        new(ChineseSimplified, "简体中文"),
    ];

    /// <summary>The language this process is showing.</summary>
    public static string Current { get; private set; } = Default;

    /// <summary>Maps any culture name to a supported language code, or null if there is none.</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var exact = Supported.FirstOrDefault(o => string.Equals(o.Code, name, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact.Code;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return ChineseSimplified;
        if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return English;
        return null;
    }

    /// <summary>The saved preference, or <see cref="Default"/> when none is saved or it cannot be read.</summary>
    public static string LoadPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
            return Normalize(key?.GetValue(ValueName) as string) ?? Default;
        }
        catch
        {
            return Default;
        }
    }

    public static void SavePreference(string code)
    {
        var normalized = Normalize(code) ?? throw new ArgumentException($"Unsupported language: {code}", nameof(code));
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        key.SetValue(ValueName, normalized, RegistryValueKind.String);
    }

    /// <summary>Sets the UI culture of this thread and of every thread started afterwards.</summary>
    public static void Apply(string code)
    {
        var normalized = Normalize(code) ?? Default;
        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Current = normalized;
    }

    /// <summary>Starts a new instance of this app with the same arguments; the caller then shuts down.</summary>
    public static void StartNewInstance()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the program path.");
        var info = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            info.ArgumentList.Add(arg);
        Process.Start(info);
    }
}
