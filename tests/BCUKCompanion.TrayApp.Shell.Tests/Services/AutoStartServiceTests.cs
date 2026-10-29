using BCUKCompanion.TrayApp.Services;
using Microsoft.Win32;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Services;

// Uses HKCU's Run key, under a value name of its own, so it only runs on Windows.
[Collection(SharedShellState.Name)]
public sealed class AutoStartServiceTests
{
    [SkippableFact]
    public void SetEnabled_AddsAndRemovesTheRunEntry()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The Run key is in the Windows registry.");
        using var folder = new TempAppFolder();

        Assert.False(AutoStartService.IsEnabled());

        AutoStartService.SetEnabled(true);
        Assert.True(AutoStartService.IsEnabled());
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            Assert.NotNull(key);
            Assert.EndsWith("\" --minimized", Assert.IsType<string>(key.GetValue(folder.Name)));
        }

        AutoStartService.SetEnabled(false);
        Assert.False(AutoStartService.IsEnabled());

        AutoStartService.SetEnabled(false); // already gone
    }
}
