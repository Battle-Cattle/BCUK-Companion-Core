using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests;

// WPF controls only exist on Windows, so each test skips elsewhere before any method touching them is compiled.
public sealed class DialogButtonsTests
{
    [SkippableFact]
    public void OkIsTheDefaultButtonAndCancelIsTheCancelButton()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WPF controls are only available on Windows.");
        RunOnSta(OkIsTheDefaultButtonAndCancelIsTheCancelButtonCore);
    }

    [SkippableFact]
    public void ClickingOkRunsTheCallback()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WPF controls are only available on Windows.");
        RunOnSta(ClickingOkRunsTheCallbackCore);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OkIsTheDefaultButtonAndCancelIsTheCancelButtonCore()
    {
        StackPanel row = DialogButtons.Create(() => { }, "Save");

        var ok = Assert.IsType<Button>(row.Children[0]);
        var cancel = Assert.IsType<Button>(row.Children[1]);
        Assert.Equal("Save", ok.Content);
        Assert.True(ok.IsDefault);
        Assert.False(ok.IsCancel);
        Assert.True(cancel.IsCancel);
        Assert.False(cancel.IsDefault);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClickingOkRunsTheCallbackCore()
    {
        int calls = 0;
        StackPanel row = DialogButtons.Create(() => calls++);

        var ok = Assert.IsType<Button>(row.Children[0]);
        ok.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(1, calls);
    }

    private static void RunOnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
