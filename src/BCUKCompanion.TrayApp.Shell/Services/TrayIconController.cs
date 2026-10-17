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

    public TrayIconController(string displayName, IReadOnlyList<TrayMenuItem>? additionalMenuItems = null)
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
            Text = displayName.Length <= 63 ? displayName : displayName[..63],
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows "&lt;displayName&gt; — &lt;status&gt;" as the tooltip. NotifyIcon.Text is limited to 63
    /// characters, so a long display name is shortened rather than the status cut off.
    /// </summary>
    public void SetStatus(string displayName, string status)
    {
        const int maxLength = 63;
        string suffix = $" — {status}";
        int maxNameLength = Math.Max(0, maxLength - suffix.Length);
        string text = (displayName.Length <= maxNameLength ? displayName : displayName[..maxNameLength]) + suffix;
        _notifyIcon.Text = text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>
    /// Best-effort: never throws, so a balloon can't break the event handling around it.
    /// ShowBalloonTip rejects empty text (e.g. an activity with no display name or detail),
    /// so that falls back to the title.
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = string.IsNullOrEmpty(text) ? title : text;
            _notifyIcon.ShowBalloonTip(5000);
        }
        catch (Exception)
        {
            // Balloons are notifications only; losing one must not affect anything else.
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
