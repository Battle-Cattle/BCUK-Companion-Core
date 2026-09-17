using System.Windows;

namespace BCUKCompanion.TrayApp;

/// <summary>
/// Builds a <see cref="TrayMenuItem"/> for opening a settings-style window: the first click
/// creates and shows it, a repeat click activates (restoring it first if minimized) the same
/// instance instead of opening a second one. Every companion app's settings menu item needs this
/// behavior, so it lives here instead of being reimplemented per app.
/// </summary>
public static class SingletonWindowMenuItem
{
    public static TrayMenuItem Create<TWindow>(string label, Func<TWindow> createWindow) where TWindow : Window
    {
        TWindow? window = null;
        return new TrayMenuItem(label, () =>
        {
            if (window is null)
            {
                window = createWindow();
                window.Closed += (_, _) => window = null;
                window.Show();
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.Activate();
        });
    }
}
