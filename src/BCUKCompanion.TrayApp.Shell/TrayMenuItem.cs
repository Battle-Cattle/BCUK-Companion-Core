namespace BCUKCompanion.TrayApp;

/// <summary>
/// An extra entry a companion app adds to the shared tray icon's context menu, e.g. to open
/// its own settings UI without resorting to a command-line flag.
/// </summary>
/// <param name="Text">The label shown for the menu entry.</param>
/// <param name="OnClick">The callback run when the entry is clicked.</param>
public sealed record TrayMenuItem(string Text, Action OnClick);
