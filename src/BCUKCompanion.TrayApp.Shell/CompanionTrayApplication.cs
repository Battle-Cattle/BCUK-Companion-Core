using System.Windows;

namespace BCUKCompanion.TrayApp;

/// <summary>
/// The full tray-app shell (single-instance guard, login/settings windows, tray icon,
/// balloon notifications). Hosts call <see cref="Run"/> from a minimal Program.cs. The work is
/// done by <see cref="TrayAppController"/>; this class only connects it to WPF.
/// </summary>
public sealed class CompanionTrayApplication : System.Windows.Application
{
    private readonly CompanionTrayAppOptions _options;
    private TrayAppController? _controller;

    /// <summary>
    /// Creates the app. It lives in the tray and keeps running until the user picks Exit, even after its last window
    /// closes; at startup it opens the login window only if there's no stored login.
    /// </summary>
    /// <param name="options">The host app's identity, integrations and update settings.</param>
    private CompanionTrayApplication(CompanionTrayAppOptions options)
    {
        _options = options;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    /// <summary>Runs the tray app until the user exits it. Call from the host's <c>[STAThread] Main</c>.</summary>
    /// <param name="options">The host app's identity, integrations and update settings; defaults suit a single companion app.</param>
    public static void Run(CompanionTrayAppOptions? options = null)
    {
        // Releases are single-file builds, and the updater never deletes anything, so an install
        // updated from an older multi-file release still holds that build's loose runtime files.
        // Runs before WPF starts so none of them are loaded yet.
        TrayAppController.RemoveLegacyInstallFiles(
            typeof(CompanionTrayApplication).Assembly.Location, Environment.ProcessPath, AppContext.BaseDirectory);
        var app = new CompanionTrayApplication(options ?? new CompanionTrayAppOptions());
        ((System.Windows.Application)app).Run();
    }

    /// <summary>Installs the last-resort error handlers, then starts the tray app (see <see cref="TrayAppController.Start"/>).</summary>
    /// <param name="e">Startup event data, including the command-line arguments (passed on to the base class only).</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _controller = new TrayAppController(
            _options, new TrayAppEnvironment(Dispatcher, Shutdown, (text, caption) => System.Windows.MessageBox.Show(text, caption)));

        // Last-resort handlers, so an exception escaping a window's event handler (a settings file
        // that can't be written, a registry error, a host window that fails to open) is reported
        // as a balloon instead of taking down the whole tray app.
        DispatcherUnhandledException += (_, args) => args.Handled = _controller.ReportUnhandledError(args.Exception);
        System.Windows.Forms.Application.ThreadException += (_, args) => _controller.ReportUnhandledError(args.Exception);

        _controller.Start();
    }

    /// <summary>Disconnects, removes the tray icon and releases the single-instance lock.</summary>
    /// <param name="e">The exit event.</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}
