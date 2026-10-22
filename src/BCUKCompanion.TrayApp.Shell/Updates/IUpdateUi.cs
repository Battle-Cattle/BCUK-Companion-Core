namespace BCUKCompanion.TrayApp.Updates;

/// <summary>
/// Everything <see cref="CompanionUpdater"/> shows the user or asks of the app, so its decisions
/// can be tested without WPF. <see cref="WpfUpdateUi"/> is the real one.
/// </summary>
internal interface IUpdateUi
{
    /// <summary>Shows an informational message the user dismisses.</summary>
    /// <param name="text">The message to show.</param>
    void ShowMessage(string text);

    /// <summary>Asks a yes/no question.</summary>
    /// <param name="text">The question to ask.</param>
    /// <returns>True when the user answered yes.</returns>
    bool Confirm(string text);

    /// <summary>Reports a problem and asks a yes/no question about it.</summary>
    /// <param name="text">The problem and the question.</param>
    /// <returns>True when the user answered yes.</returns>
    bool ConfirmWarning(string text);

    /// <summary>Shows a tray balloon.</summary>
    /// <param name="title">The balloon's title.</param>
    /// <param name="text">The balloon's text.</param>
    void ShowBalloon(string title, string text);

    /// <summary>Opens a web page in the user's browser.</summary>
    /// <param name="url">The https page to open.</param>
    void OpenUrl(Uri url);

    /// <summary>Shuts the app down once it's idle, so the update script can replace it.</summary>
    void Shutdown();
}
