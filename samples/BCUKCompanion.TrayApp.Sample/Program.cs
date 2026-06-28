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
            OnBotEvent = e => Console.WriteLine($"Bot event: {e.EventName}"),
            AdditionalMenuItems = new[]
            {
                new TrayMenuItem("Sample Settings...", () => Console.WriteLine("Sample settings clicked"))
            }
        });
    }
}
