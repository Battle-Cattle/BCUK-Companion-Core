using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace BCUKCompanion.TrayApp.Updates;

/// <summary>The updater's prompts as WPF message boxes, its balloons through the tray icon, and shutdown through the WPF application.</summary>
/// <param name="caption">The message boxes' title, e.g. <c>MyApp updates</c>.</param>
/// <param name="showBalloon">Shows a tray balloon (title, text).</param>
internal sealed class WpfUpdateUi(string caption, Action<string, string> showBalloon) : IUpdateUi
{
    /// <inheritdoc/>
    public void ShowMessage(string text) => MessageBox.Show(text, caption);

    /// <inheritdoc/>
    public bool Confirm(string text) =>
        MessageBox.Show(text, caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <inheritdoc/>
    public bool ConfirmWarning(string text) =>
        MessageBox.Show(text, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <inheritdoc/>
    public void ShowBalloon(string title, string text) => showBalloon(title, text);

    /// <inheritdoc/>
    public void OpenUrl(Uri url) =>
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();

    /// <inheritdoc/>
    public void Shutdown() =>
        // Once idle, so queued work like the activity-id save runs first; the update script
        // waits for this process to exit.
        _ = Application.Current.Dispatcher.InvokeAsync(
            () => Application.Current.Shutdown(), DispatcherPriority.ApplicationIdle);
}
