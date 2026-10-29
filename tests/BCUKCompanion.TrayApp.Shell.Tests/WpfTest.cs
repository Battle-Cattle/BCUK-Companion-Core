using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests;

/// <summary>Skips a test that needs WPF, which only exists on Windows. Holds no WPF types, so it loads anywhere.</summary>
internal static class WindowsOnly
{
    public static void Skip() => Xunit.Skip.IfNot(OperatingSystem.IsWindows(), "WPF only runs on Windows.");
}

/// <summary>
/// Runs WPF tests on an STA thread with a running dispatcher. WPF only exists on Windows, so every
/// caller skips first on other platforms (CI's Windows job runs them), and keeps the WPF code in a
/// separate method so it's never compiled where WPF can't load.
/// </summary>
internal static class WpfTest
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Runs <paramref name="test"/> on a fresh STA thread, pumping its dispatcher until the returned task ends.</summary>
    public static void Run(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            try
            {
                var task = test();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
                Dispatcher.PushFrame(frame);

                // Rethrows a fault or a cancellation, so neither passes as success.
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TestTimeout))
        {
            throw new TimeoutException($"The WPF test didn't finish within {TestTimeout.TotalSeconds}s.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public static void Run(Action test) => Run(() =>
    {
        test();
        return Task.CompletedTask;
    });

    /// <summary>
    /// Shows <paramref name="window"/> as a dialog, runs <paramref name="drive"/> once it has loaded,
    /// and closes it if <paramref name="drive"/> didn't. Returns only once <paramref name="drive"/> has
    /// finished, even if it closed the dialog part-way, and rethrows anything it threw.
    /// </summary>
    public static bool? ShowDialog(Window window, Func<Window, Task> drive)
    {
        Exception? failure = null;
        var driven = new TaskCompletionSource();
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Loaded += (_, _) => window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await drive(window);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }

                driven.TrySetResult();
            }
        });

        var result = window.ShowDialog();
        if (!driven.Task.IsCompleted)
        {
            // The dialog closed while the driver was still awaiting something: let it finish.
            var frame = new DispatcherFrame();
            driven.Task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }

    public static bool? ShowDialog(Window window, Action<Window> drive) => ShowDialog(window, w =>
    {
        drive(w);
        return Task.CompletedTask;
    });

    /// <summary>Waits, letting the dispatcher run, until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never held.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Every element under <paramref name="root"/> in the logical tree, root included.</summary>
    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    public static IEnumerable<T> All<T>(DependencyObject root) => Descendants(root).OfType<T>();

    public static Button Button(DependencyObject root, string content) =>
        All<Button>(root).Single(b => Equals(b.Content, content));

    public static void Click(DependencyObject root, string content) => Click(Button(root, content));

    public static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    public static IEnumerable<string> Texts(DependencyObject root) => All<TextBlock>(root).Select(t => t.Text);
}
