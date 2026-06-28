using System.IO;

namespace BCUKCompanion.TrayApp;

/// <summary>Per-user data file locations under %APPDATA%/%LOCALAPPDATA%.</summary>
internal static class AppPaths
{
    private static string _folderName = "BCUKCompanion";

    public static void Configure(string folderName) => _folderName = folderName;

    public static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _folderName, "settings.json");

    public static string TokenFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _folderName, "token.bin");
}
