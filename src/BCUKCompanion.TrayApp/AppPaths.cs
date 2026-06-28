namespace BCUKCompanion.TrayApp;

/// <summary>Per-user data file locations under %APPDATA%/%LOCALAPPDATA%.</summary>
internal static class AppPaths
{
    private const string FolderName = "BCUKCompanion";

    public static string SettingsFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName, "settings.json");

    public static string TokenFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName, "token.bin");
}
