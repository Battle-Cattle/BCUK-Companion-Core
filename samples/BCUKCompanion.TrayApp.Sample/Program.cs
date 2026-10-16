using System.Linq;
using BCUKCompanion.TrayApp;

namespace BCUKCompanion.TrayApp.Sample;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        CompanionTrayApplication.Run(new CompanionTrayAppOptions
        {
            DataFolderName = "BCUKCompanion.TrayApp.Sample",
            OnBotEvent = e => Console.WriteLine($"Bot event: {e.EventName} ({string.Join(", ", e.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))})"),
            // No actions are configured in the sample, so every advanced command is acknowledged
            // as a failure (and the viewer refunded); a real app returns its dispatcher's result.
            OnBotEventAsync = _ => Task.FromResult<BCUKCompanion.Core.Actions.EventDispatchResult?>(null),
            AdditionalMenuItems = new[]
            {
                new TrayMenuItem("Sample Settings...", () => Console.WriteLine("Sample settings clicked"))
            }
        });
    }
}
