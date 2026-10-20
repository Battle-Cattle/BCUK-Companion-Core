using System.IO;
using Microsoft.Win32;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>Registers/unregisters the app to start in the background with Windows login.</summary>
internal static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string _valueName = "BCUKCompanion";

    /// <summary>Sets the Run-key value name (the app's data folder name, so apps don't overwrite each other's entry); call once at startup.</summary>
    public static void Configure(string valueName) => _valueName = valueName;

    /// <summary>Adds or removes the current user's Run-key entry, which starts the app minimized at sign-in.</summary>
    /// <param name="enabled">Whether the app should start with Windows.</param>
    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            // CreateSubKey opens the key if it exists and creates it (plus any
            // missing ancestors) if it doesn't -- OpenSubKey would instead return
            // null and silently no-op on a profile where Run hasn't been created yet.
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            string exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            key.SetValue(_valueName, $"\"{exePath}\" --minimized");
        }
        else
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Whether the current user's Run key has this app's entry.</summary>
    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(_valueName) is not null;
    }
}
