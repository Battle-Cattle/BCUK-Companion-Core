using System.Linq;
using BCUKCompanion.TrayApp;

namespace BCUKCompanion.TrayApp.Sample;

/// <summary>The sample host's entry point.</summary>
internal static class Program
{
    /// <summary>Runs the shared tray app with the sample's own identity and a test menu item, as a minimal per-user app would.</summary>
    [STAThread]
    private static void Main()
    {
        CompanionTrayApplication.Run(new CompanionTrayAppOptions
        {
            DataFolderName = "BCUKCompanion.TrayApp.Sample",
            DisplayName = "BCUK Companion Sample",
            OnBotEvent = e => Console.WriteLine($"Bot event: {e.EventName} ({string.Join(", ", e.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))})"),
            // No actions are configured in the sample, so it sets no CreateEventActionDispatcher and
            // every advanced command is acknowledged as a failure (the viewer is refunded). A real
            // app sets CreateEventActionDispatcher to a dispatcher over its own action kinds.
            AdditionalMenuItems = new[]
            {
                new TrayMenuItem("Sample Settings...", () => Console.WriteLine("Sample settings clicked"))
            }
        });
    }
}
