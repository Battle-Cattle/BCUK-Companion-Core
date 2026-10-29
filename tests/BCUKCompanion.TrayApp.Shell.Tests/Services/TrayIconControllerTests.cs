using System.Windows.Forms;
using BCUKCompanion.TrayApp.Services;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Services;

// NotifyIcon is WinForms, which only exists on Windows.
public sealed class TrayIconControllerTests
{
    private static ToolStripItemCollection MenuItems(TrayIconController tray) =>
        Assert.IsType<ContextMenuStrip>(tray.NotifyIcon.ContextMenuStrip).Items;

    private static void Click(TrayIconController tray, string label) =>
        MenuItems(tray).Cast<ToolStripItem>().Single(i => i.Text == label).PerformClick();

    [SkippableFact]
    public void Menu_ListsTheBuiltInItemsAroundTheHostsOwn()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            using var tray = new TrayIconController("Companion", [new TrayMenuItem("Actions...", () => { })]);

            var items = MenuItems(tray).Cast<ToolStripItem>()
                .Select(i => i is ToolStripSeparator ? "-" : i.Text);
            Assert.Equal(["Login / Account", "Settings", "-", "Actions...", "-", "Exit"], items);
            Assert.Equal("Companion", tray.NotifyIcon.Text);
            Assert.True(tray.NotifyIcon.Visible);
        });
    }

    [SkippableFact]
    public void Menu_WithoutHostItems_HasOneSeparator()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            using var tray = new TrayIconController("Companion");

            Assert.Equal(4, MenuItems(tray).Count);
        });
    }

    [SkippableFact]
    public void MenuItems_RaiseTheirEvents()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var raised = new List<string>();
            using var tray = new TrayIconController("Companion", [new TrayMenuItem("Actions...", () => raised.Add("actions"))]);
            tray.OpenLoginRequested += (_, _) => raised.Add("login");
            tray.OpenSettingsRequested += (_, _) => raised.Add("settings");
            tray.ExitRequested += (_, _) => raised.Add("exit");

            Click(tray, "Login / Account");
            Click(tray, "Settings");
            Click(tray, "Actions...");
            Click(tray, "Exit");
            tray.OnIconDoubleClicked();

            Assert.Equal(["login", "settings", "actions", "exit", "settings"], raised);
        });
    }

    [SkippableFact]
    public void AMenuItemThatThrows_IsReportedNotRaised()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            using var tray = new TrayIconController("Companion", [new TrayMenuItem("Broken", () => throw new InvalidOperationException("boom"))]);
            (string Label, Exception Error)? failure = null;
            tray.MenuItemFailed += (_, f) => failure = f;

            Click(tray, "Broken");

            Assert.True(failure.HasValue);
            var (label, error) = failure.Value;
            Assert.Equal("Broken", label);
            Assert.Equal("boom", error.Message);
        });
    }

    [SkippableFact]
    public void Text_IsCutToTheTrayLimit()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var longName = new string('n', 80);
            using var tray = new TrayIconController(longName);
            Assert.Equal(63, tray.NotifyIcon.Text.Length);

            tray.SetStatus("Companion", "Connected");
            Assert.Equal("Companion — Connected", tray.NotifyIcon.Text);

            tray.SetStatus(longName, "Connected");
            Assert.Equal(63, tray.NotifyIcon.Text.Length);
            Assert.EndsWith(" — Connected", tray.NotifyIcon.Text);

            tray.SetStatus("Companion", new string('s', 80));
            Assert.Equal(63, tray.NotifyIcon.Text.Length);
        });
    }

    [SkippableFact]
    public void ShowBalloon_UsesTheTitleWhenThereIsNoText()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            using var tray = new TrayIconController("Companion");

            tray.ShowBalloon("Title", "Body");
            Assert.Equal(("Title", "Body"), (tray.NotifyIcon.BalloonTipTitle, tray.NotifyIcon.BalloonTipText));

            tray.ShowBalloon("Only a title", "");
            Assert.Equal("Only a title", tray.NotifyIcon.BalloonTipText);
        });
    }

    [SkippableFact]
    public void ShowBalloon_AfterDispose_IsIgnored()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var tray = new TrayIconController("Companion");
            tray.Dispose();

            tray.ShowBalloon("Title", "Body");
        });
    }
}
