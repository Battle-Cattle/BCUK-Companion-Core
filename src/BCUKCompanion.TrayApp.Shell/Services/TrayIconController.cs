using System.Windows.Forms;

namespace BCUKCompanion.TrayApp.Services;

/// <summary>
/// Owns the taskbar notification-area icon and its context menu. This is
/// what keeps the app "running in the background" — no window is shown
/// unless the user opens one from this menu.
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public event EventHandler? OpenLoginRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconController(IReadOnlyList<TrayMenuItem>? additionalMenuItems = null)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Login / Account", null, (_, _) => OpenLoginRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Settings", null, (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));

        if (additionalMenuItems is { Count: > 0 })
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (TrayMenuItem item in additionalMenuItems)
            {
                menu.Items.Add(item.Text, null, (_, _) =>
                {
                    try
                    {
                        item.OnClick();
                    }
                    catch (Exception)
                    {
                        // Host-supplied callback — don't let it take down the shared tray process.
                    }
                });
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "BCUK Companion",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetStatus(string tooltipText)
    {
        // NotifyIcon.Text is limited to 63 characters.
        _notifyIcon.Text = tooltipText.Length <= 63 ? tooltipText : tooltipText[..63];
    }

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
