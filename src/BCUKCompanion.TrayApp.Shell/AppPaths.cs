using System.IO;

namespace BCUKCompanion.TrayApp;

/// <summary>Per-user data file locations under %APPDATA%/%LOCALAPPDATA%.</summary>
internal static class AppPaths
{
    private static string _folderName = "BCUKCompanion";

    /// <summary>Sets the app's data folder name (see <see cref="CompanionTrayAppOptions.DataFolderName"/>); call once at startup.</summary>
    public static void Configure(string folderName) => _folderName = folderName;

    /// <summary>The settings file, under %APPDATA% (roams with the user's profile).</summary>
    public static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _folderName, "settings.json");

    /// <summary>The encrypted token file, under %LOCALAPPDATA% (DPAPI-encrypted for this machine, so it doesn't roam).</summary>
    public static string TokenFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _folderName, "token.bin");
}
