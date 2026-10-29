using System.Windows;
using System.Windows.Controls;
using BCUKCompanion.Core;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.TrayApp.Actions;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Actions;

public sealed class EventActionMappingsWindowTests : IDisposable
{
    private readonly string folderName = $"BCUKCompanion.TrayApp.Shell.Tests.{Guid.NewGuid():N}";
    private readonly TestStore store;

    public EventActionMappingsWindowTests()
    {
        store = new TestStore(folderName);
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(store.ConfigFilePath) ?? throw new InvalidOperationException("The config file has no folder.");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public sealed class TestConfig : IEventActionMappingsConfig
    {
        public List<EventActionMapping> Mappings { get; init; } = [];
    }

    private sealed class TestStore(string folderName)
        : EventActionConfigStore<TestConfig>(folderName, "mappings.json", new EventActionTypeRegistry());

    /// <summary>A minimal app window: the footer and the mappings tab, with its protected parts opened up.</summary>
    private sealed class TestWindow : EventActionMappingsWindow<TestConfig>
    {
        public TestWindow(TestStore store, TestConfig config, Func<CompanionClient?>? getCompanionClient = null)
            : base(store, config, getCompanionClient)
        {
            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            Content = new DockPanel { Children = { footer, BuildMappingsTab() } };
            RefreshRewardTitleSuggestions();
            ShowTestResults = (report, caption) => Results.Add((report, caption));
        }

        public Func<IEventActionContext> Context { get; set; } = () => new EmptyContext();

        public List<(string Report, string Caption)> Results { get; } = [];

        public ListBox MappingList => MappingsList;

        public ListBox ActionList => ActionsList;

        public ComboBox TitleCombo => RewardTitleCombo;

        public ComboBox KindCombo => TriggerKindCombo;

        public string Status => StatusText.Text;

        public IReadOnlyList<EventActionMapping> CurrentMappings => Mappings;

        public int Remove(Func<IEventAction, bool> shouldRemove) => RemoveActions(shouldRemove);

        public void Replace(EventActionMapping mapping, List<IEventAction> actions) => ReplaceMappingActions(mapping, actions);

        public void Click(string button) => WpfTest.Click(this, button);

        public void Select(int mapping) => MappingList.SelectedIndex = mapping;

        /// <summary>Answers the next Add/Edit Action dialog by typing a delay and clicking OK (or closing it, for null).</summary>
        public void AnswerDialogWithDelay(string? seconds) => ShowDialogWindow = dialog => WpfTest.ShowDialog(dialog, window =>
        {
            if (seconds is not null)
            {
                WpfTest.All<TextBox>(window).Single().Text = seconds;
                WpfTest.Click(window, "OK");
            }
        });

        protected override TestConfig BuildConfig() => new() { Mappings = Mappings.ToList() };

        protected override IEventActionContext BuildContext() => Context();
    }

    private static TestConfig Config(params EventActionMapping[] mappings) => new() { Mappings = [.. mappings] };

    private static EventActionMapping Mapping(string title, params IEventAction[] actions) => new(title, actions);

    [SkippableFact]
    public void AddMapping_AddsAndSelectsIt_OrExplainsWhatsMissing()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate")));

            window.Click("Add Mapping");
            Assert.Equal("Enter a reward title or command name first.", window.Status);

            window.TitleCombo.Text = "Stretch";
            window.Click("Add Mapping");
            Assert.Equal(["Hydrate", "Stretch"], window.CurrentMappings.Select(m => m.RewardTitle));
            Assert.Equal("Stretch", ((EventActionMapping)window.MappingList.SelectedItem).RewardTitle);
            Assert.Contains("Stretch", (IEnumerable<string>)window.TitleCombo.ItemsSource);

            window.TitleCombo.Text = "hydrate";
            window.Click("Add Mapping");
            Assert.Equal(2, window.CurrentMappings.Count);
            Assert.Equal("Hydrate", ((EventActionMapping)window.MappingList.SelectedItem).RewardTitle);
        });
    }

    [SkippableFact]
    public void RemoveMapping_RemovesTheSelectedOne()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate"), Mapping("Stretch")));

            window.Click("Remove Mapping");
            Assert.Equal("Select a mapping to remove.", window.Status);

            window.Select(0);
            window.Click("Remove Mapping");
            Assert.Equal(["Stretch"], window.CurrentMappings.Select(m => m.RewardTitle));
            Assert.Null(window.ActionList.ItemsSource);
        });
    }

    [SkippableFact]
    public void ActionButtons_NeedAMappingAndAnAction()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate", new DelayAction())));
            var statuses = new List<string>();

            foreach (var button in new[] { "Add Action", "Edit Action", "Remove Action", "Test" })
            {
                window.Click(button);
                statuses.Add(window.Status);
            }

            window.Select(0);
            window.Click("Edit Action");
            statuses.Add(window.Status);
            window.Click("Remove Action");
            statuses.Add(window.Status);

            Assert.Equal(
                [
                    "Select a mapping first.", "Select a mapping first.", "Select a mapping first.", "Select a mapping to test.",
                    "Select an action to edit.", "Select an action to remove.",
                ],
                statuses);
        });
    }

    [SkippableFact]
    public void AddEditAndRemoveAction_ChangeTheSelectedMappingsActions()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            // The dialog's Owner must be a window that has been shown.
            var window = new TestWindow(store, Config(Mapping("Hydrate"))) { ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            window.Select(0);

            window.AnswerDialogWithDelay("3");
            window.Click("Add Action");
            window.AnswerDialogWithDelay("4");
            window.Click("Add Action");
            Assert.Equal(["Delay 3s", "Delay 4s"], window.ActionList.Items.Cast<object>().Select(i => i.ToString()));

            window.ActionList.SelectedIndex = 0;
            window.AnswerDialogWithDelay("7");
            window.Click("Edit Action");
            Assert.Equal(["Delay 7s", "Delay 4s"], window.ActionList.Items.Cast<object>().Select(i => i.ToString()));

            window.ActionList.SelectedIndex = 1;
            window.AnswerDialogWithDelay(null);
            window.Click("Edit Action");
            window.Click("Add Action");
            Assert.Equal(2, window.ActionList.Items.Count);

            window.ActionList.SelectedIndex = 1;
            window.Click("Remove Action");
            Assert.Equal(["Delay 7s"], window.ActionList.Items.Cast<object>().Select(i => i.ToString()));
            Assert.Equal("Hydrate", ((EventActionMapping)window.MappingList.SelectedItem).RewardTitle);
            window.Close();
        });
    }

    [SkippableFact]
    public void Test_RunsTheMappingAndReportsEachAction()
    {
        WindowsOnly.Skip();
        WpfTest.Run(async () =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate", new StubAction(), new StubAction(succeeds: false, kind: "broken"))));
            window.Select(0);

            window.Click("Test");
            await WpfTest.WaitUntil(() => window.Results.Count > 0);

            var (report, caption) = Assert.Single(window.Results);
            Assert.Equal("Test results: Hydrate", caption);
            Assert.StartsWith("stub: OK\nbroken: ", report);
            Assert.Equal("Test completed with errors.", window.Status);
        });
    }

    [SkippableFact]
    public void Test_ReportsSuccess()
    {
        WindowsOnly.Skip();
        WpfTest.Run(async () =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate", new StubAction())));
            window.Select(0);

            window.Click("Test");
            await WpfTest.WaitUntil(() => window.Status == "Test succeeded.");
        });
    }

    [SkippableFact]
    public void Test_WithNoActions_SaysNothingRan()
    {
        WindowsOnly.Skip();
        WpfTest.Run(async () =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate")));
            window.Select(0);

            window.Click("Test");
            await WpfTest.WaitUntil(() => window.Status == "Test did not dispatch any actions.");
            Assert.Empty(window.Results);
        });
    }

    [SkippableFact]
    public void Test_ThatThrows_IsReportedInTheStatusLine()
    {
        WindowsOnly.Skip();
        WpfTest.Run(async () =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate", new StubAction())));
            window.Select(0);
            window.Context = () => throw new InvalidOperationException("no context");

            window.Click("Test");
            await WpfTest.WaitUntil(() => window.Status.StartsWith("Test failed:"));
            Assert.Equal("Test failed: no context", window.Status);
        });
    }

    [SkippableFact]
    public void Save_WritesTheMappings_OrSaysWhyItCouldnt()
    {
        WindowsOnly.Skip();
        WpfTest.Run(async () =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate", new DelayAction { DelaySeconds = 2 })));

            window.Click("Save");
            await WpfTest.WaitUntil(() => window.Status.StartsWith("Saved to"));
            Assert.True(window.IsEnabled);
            Assert.Equal("Hydrate", Assert.Single(store.Load().Mappings).RewardTitle);

            File.Delete(store.ConfigFilePath);
            Directory.CreateDirectory(store.ConfigFilePath); // a folder where the file should go
            window.Click("Save");
            await WpfTest.WaitUntil(() => window.Status.StartsWith("Save failed:"));
            Assert.True(window.IsEnabled);
        });
    }

    [SkippableFact]
    public void RemoveActions_DropsMatchingActionsAndKeepsTheSelection()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate", new StubAction(), new DelayAction { DelaySeconds = 5 }), Mapping("Stretch", new StubAction())));
            window.Select(0);

            Assert.Equal(0, window.Remove(a => a.Kind == "nothing"));
            Assert.Equal(2, window.Remove(a => a is StubAction));

            Assert.Equal(0, window.MappingList.SelectedIndex);
            Assert.Equal(["Delay 5s"], window.ActionList.Items.Cast<object>().Select(i => i.ToString()));
        });
    }

    [SkippableFact]
    public void ReplaceMappingActions_KeepsTheMappingSelected()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var window = new TestWindow(store, Config(Mapping("Hydrate"), Mapping("Stretch")));
            window.Select(0);

            window.Replace(window.CurrentMappings[0], [new DelayAction()]);
            Assert.Equal(0, window.MappingList.SelectedIndex);

            window.Replace(window.CurrentMappings[1], [new DelayAction()]);
            Assert.Equal(0, window.MappingList.SelectedIndex);
            Assert.Single(window.CurrentMappings[1].Actions);
        });
    }

    [SkippableFact]
    public void Suggestions_ComeFromTheBotOnceLoggedIn()
    {
        WindowsOnly.Skip();
        var handler = new RouteHttpHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/companion/rewards" => RouteHttpHandler.Json(
                """{"ok":true,"rewards":[{"id":"1","title":"Hydrate","isEnabled":true},{"id":"2","title":"Paused","isEnabled":false}]}"""),
            "/api/companion/commands" => RouteHttpHandler.Json(
                """{"ok":true,"commands":[{"id":1,"name":"hug","trigger":"!hug","companionEnabled":true},{"id":2,"name":"quiet","trigger":"!quiet","companionEnabled":false}]}"""),
            _ => RouteHttpHandler.Json("{}", System.Net.HttpStatusCode.NotFound),
        });
        using var client = new CompanionClient(new Uri("https://bot.example"), new MemoryTokenStore("token"), new HttpClient(handler));
        WpfTest.Run(async () =>
        {
            var window = new TestWindow(store, Config(Mapping("Mapped")), () => client);
            IEnumerable<string> Suggestions() => (IEnumerable<string>)window.TitleCombo.ItemsSource;

            await WpfTest.WaitUntil(() => Suggestions().Contains("Hydrate"));
            Assert.DoesNotContain("Paused", Suggestions());
            Assert.Contains("Mapped", Suggestions());

            window.TitleCombo.Text = "typed";
            window.KindCombo.SelectedItem = EventActionTriggerKind.Command;
            await WpfTest.WaitUntil(() => Suggestions().Contains("hug"));
            Assert.DoesNotContain("quiet", Suggestions());
            Assert.Equal("typed", window.TitleCombo.Text);
        });
    }

    [SkippableFact]
    public void Suggestions_StayLocalWhenTheBotFails()
    {
        WindowsOnly.Skip();
        var handler = new RouteHttpHandler(_ => RouteHttpHandler.Json("{}", System.Net.HttpStatusCode.InternalServerError));
        using var client = new CompanionClient(new Uri("https://bot.example"), new MemoryTokenStore("token"), new HttpClient(handler));
        WpfTest.Run(async () =>
        {
            var window = new TestWindow(store, Config(Mapping("Mapped")), () => client);

            await window.PendingServerRefresh;

            Assert.Single(handler.Paths);
            Assert.Equal(["Mapped"], (IEnumerable<string>)window.TitleCombo.ItemsSource);
        });
    }

    [SkippableFact]
    public void Suggestions_FromAnOlderRefreshAreDropped()
    {
        WindowsOnly.Skip();
        var release = new TaskCompletionSource();
        var handler = new RouteHttpHandler(_ =>
        {
            release.Task.Wait(TimeSpan.FromSeconds(10));
            return RouteHttpHandler.Json("""{"ok":true,"rewards":[{"id":"1","title":"Late","isEnabled":true}]}""");
        });
        using var client = new CompanionClient(new Uri("https://bot.example"), new MemoryTokenStore("token"), new HttpClient(handler));
        var loggedIn = true;
        WpfTest.Run(async () =>
        {
            var window = new TestWindow(store, Config(Mapping("Mapped")), () => loggedIn ? client : null);
            var staleFetch = window.PendingServerRefresh;

            loggedIn = false;
            window.KindCombo.SelectedItem = EventActionTriggerKind.Command; // a newer, local-only refresh
            release.SetResult();
            await staleFetch;

            Assert.Single(handler.Paths);
            Assert.Empty((IEnumerable<string>)window.TitleCombo.ItemsSource);
        });
    }
}
