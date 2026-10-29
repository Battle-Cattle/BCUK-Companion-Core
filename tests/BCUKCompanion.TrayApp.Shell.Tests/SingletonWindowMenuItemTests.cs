using System.Windows;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests;

public sealed class SingletonWindowMenuItemTests
{
    [SkippableFact]
    public void Click_OpensOneWindow_ThenBringsItBack_AndOpensANewOneOnceClosed()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var created = new List<Window>();
            var item = SingletonWindowMenuItem.Create("Actions...", () =>
            {
                var window = new Window { ShowActivated = false, ShowInTaskbar = false };
                created.Add(window);
                return window;
            });

            Assert.Equal("Actions...", item.Text);
            item.OnClick();
            Assert.True(Assert.Single(created).IsVisible);

            created[0].WindowState = WindowState.Minimized;
            item.OnClick();
            Assert.Single(created);
            Assert.Equal(WindowState.Normal, created[0].WindowState);

            item.OnClick();
            Assert.Single(created);

            created[0].Close();
            item.OnClick();
            Assert.Equal(2, created.Count);
            created[1].Close();
        });
    }
}
