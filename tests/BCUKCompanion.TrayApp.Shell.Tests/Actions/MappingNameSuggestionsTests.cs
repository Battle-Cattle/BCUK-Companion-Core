using System.Net;
using BCUKCompanion.Core;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.TrayApp.Actions;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Actions;

public sealed class MappingNameSuggestionsTests : IDisposable
{
    private const string Rewards = """
        {"ok":true,"rewards":[
          {"id":"1","title":"zebra","isEnabled":true},
          {"id":"2","title":"Paused","isEnabled":false},
          {"id":"3","title":"lights","isEnabled":true}]}
        """;

    private const string Commands = """
        {"ok":true,"commands":[
          {"id":1,"name":"Hydrate","companionEnabled":true},
          {"id":2,"name":"ChatOnly","companionEnabled":false}]}
        """;

    private readonly EventActionMappingsEditor editor = new(
    [
        new EventActionMapping("Lights", []),
        new EventActionMapping("Alpha", []),
        new EventActionMapping("Stretch", [], EventActionTriggerKind.Command),
    ]);

    private readonly List<CompanionClient> clients = [];
    private readonly List<List<string>> mapped = [];
    private readonly List<List<string>> live = [];

    public void Dispose()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    private CompanionClient Client(HttpMessageHandler handler, string? token = "token")
    {
        var client = new CompanionClient(new Uri("https://bot.example.com"), new MemoryTokenStore(token), new HttpClient(handler));
        clients.Add(client);
        return client;
    }

    private static RouteHttpHandler Answering(string rewards = Rewards, string commands = Commands) =>
        new(request => RouteHttpHandler.Json(request.RequestUri!.AbsolutePath == "/api/companion/commands" ? commands : rewards));

    private Task? Refresh(MappingNameSuggestions suggestions, EventActionTriggerKind kind = EventActionTriggerKind.Reward) =>
        suggestions.Refresh(kind, mapped.Add, live.Add);

    [Fact]
    public void WithoutAClient_ShowsOnlyTheMappedNames()
    {
        var suggestions = new MappingNameSuggestions(editor, null);

        Assert.Null(Refresh(suggestions));

        Assert.Equal(["Lights", "Alpha"], Assert.Single(mapped));
        Assert.Empty(live);
    }

    [Fact]
    public void WithALoggedOutClient_DoesNotFetch()
    {
        var handler = Answering();
        var suggestions = new MappingNameSuggestions(editor, () => Client(handler, token: null));

        Assert.Null(Refresh(suggestions, EventActionTriggerKind.Command));

        Assert.Equal(["Stretch"], Assert.Single(mapped));
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task Rewards_MergeTheEnabledLiveTitlesWithTheMappedOnes()
    {
        var suggestions = new MappingNameSuggestions(editor, () => Client(Answering()));

        await Refresh(suggestions)!;

        Assert.Equal(["Lights", "Alpha"], Assert.Single(mapped));
        // The live casing wins over the mapped one.
        Assert.Equal(["Alpha", "lights", "zebra"], Assert.Single(live));
    }

    [Fact]
    public async Task Commands_OfferOnlyTheOnesThatSendEventsToTheApp()
    {
        var suggestions = new MappingNameSuggestions(editor, () => Client(Answering()));

        await Refresh(suggestions, EventActionTriggerKind.Command)!;

        Assert.Equal(["Hydrate", "Stretch"], Assert.Single(live));
    }

    [Fact]
    public async Task AFailedFetch_LeavesTheMappedNames()
    {
        var handler = new RouteHttpHandler(_ => RouteHttpHandler.Json("{}", HttpStatusCode.InternalServerError));
        var suggestions = new MappingNameSuggestions(editor, () => Client(handler));

        await Refresh(suggestions)!;

        Assert.Single(mapped);
        Assert.Empty(live);
    }

    [Fact]
    public async Task AFetchSupersededByALaterRefresh_IsDropped()
    {
        var release = new TaskCompletionSource();
        var handler = new RouteHttpHandler(_ =>
        {
            release.Task.Wait();
            return RouteHttpHandler.Json(Rewards);
        });
        CompanionClient? client = Client(handler);
        var suggestions = new MappingNameSuggestions(editor, () => client);

        Task first = Refresh(suggestions)!;
        client = null; // the second refresh finds no client, which still supersedes the first
        Assert.Null(Refresh(suggestions));
        release.SetResult();
        await first;

        Assert.Equal(2, mapped.Count);
        Assert.Empty(live);
    }
}
