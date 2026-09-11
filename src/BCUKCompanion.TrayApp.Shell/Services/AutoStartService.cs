using System.IO;
using Microsoft.Win32;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>Registers/unregisters the app to start in the background with Windows login.</summary>
internal static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string _valueName = "BCUKCompanion";

    public static void Configure(string valueName) => _valueName = valueName;

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

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(_valueName) is not null;
    }
}
