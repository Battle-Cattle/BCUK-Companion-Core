namespace BCUKCompanion.TrayApp;

/// <summary>
/// An extra entry a companion app adds to the shared tray icon's context menu, e.g. to open
/// its own settings UI without resorting to a command-line flag.
/// </summary>
public sealed record TrayMenuItem(string Text, Action OnClick);
