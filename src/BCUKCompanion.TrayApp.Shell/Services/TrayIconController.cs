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

    /// <summary>Raised when the user picks "Login / Account".</summary>
    public event EventHandler? OpenLoginRequested;
    /// <summary>Raised when the user picks "Settings".</summary>
    public event EventHandler? OpenSettingsRequested;
    /// <summary>Raised when the user picks "Exit".</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when a menu item's handler throws, with the item's label and the error.</summary>
    public event EventHandler<(string Label, Exception Error)>? MenuItemFailed;

    /// <summary>Shows the tray icon with its context menu.</summary>
    /// <param name="displayName">The icon's tooltip.</param>
    /// <param name="additionalMenuItems">The host app's own items, shown between the built-in ones and Exit.</param>
    public TrayIconController(string displayName, IReadOnlyList<TrayMenuItem>? additionalMenuItems = null)
    {
        var menu = new ContextMenuStrip();
        AddItem(menu, "Login / Account", () => OpenLoginRequested?.Invoke(this, EventArgs.Empty));
        AddItem(menu, "Settings", () => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));

        if (additionalMenuItems is { Count: > 0 })
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (TrayMenuItem item in additionalMenuItems)
            {
                AddItem(menu, item.Text, item.OnClick);
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        AddItem(menu, "Exit", () => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = displayName.Length <= 63 ? displayName : displayName[..63],
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => OnIconDoubleClicked();
    }

    /// <summary>The underlying tray icon, for tests to read its text, menu and balloon.</summary>
    internal NotifyIcon NotifyIcon => _notifyIcon;

    /// <summary>Double-clicking the icon opens Settings, like its menu item.</summary>
    internal void OnIconDoubleClicked() =>
        RunSafely("Settings", () => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));

    /// <summary>Adds a menu item whose click runs <paramref name="onClick"/> through <see cref="RunSafely"/>.</summary>
    /// <param name="menu">The context menu to add the item to.</param>
    /// <param name="label">The item's text, also reported if its action fails.</param>
    /// <param name="onClick">What the item does when clicked.</param>
    private void AddItem(ContextMenuStrip menu, string label, Action onClick) =>
        menu.Items.Add(label, null, (_, _) => RunSafely(label, onClick));

    // A menu click runs on the WinForms message loop, where an escaping exception would bring up
    // WinForms' crash dialog: report it through MenuItemFailed instead and keep the tray running.
    /// <summary>Runs a menu item's action, raising <see cref="MenuItemFailed"/> instead of letting an exception escape.</summary>
    /// <param name="label">The menu item's text, passed to <see cref="MenuItemFailed"/> on failure.</param>
    /// <param name="action">The item's action.</param>
    private void RunSafely(string label, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MenuItemFailed?.Invoke(this, (label, ex));
        }
    }

    /// <summary>
    /// Shows "&lt;displayName&gt; — &lt;status&gt;" as the tooltip. NotifyIcon.Text is limited to 63
    /// characters, so a long display name is shortened rather than the status cut off.
    /// </summary>
    /// <param name="displayName">The app's name, truncated if needed.</param>
    /// <param name="status">The connection status text, e.g. "Connected".</param>
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
    /// <param name="title">The balloon's title.</param>
    /// <param name="text">The balloon's body; the title is shown instead when it's null or empty.</param>
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

    /// <summary>Removes the icon from the notification area.</summary>
    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
