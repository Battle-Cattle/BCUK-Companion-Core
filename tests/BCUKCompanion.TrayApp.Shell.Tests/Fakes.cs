using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Tokens;

namespace BCUKCompanion.TrayApp.Tests;

/// <summary>Answers every request with one status and body, recording each request's path and body.</summary>
internal sealed class RecordingHttpHandler(HttpStatusCode status = HttpStatusCode.OK, string body = """{"ok":true,"resolved":true}""")
    : HttpMessageHandler
{
    public ConcurrentQueue<(string Path, JsonElement? Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.RequestUri!.AbsolutePath, text is null ? null : JsonDocument.Parse(text).RootElement.Clone()));
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

/// <summary>An in-memory token store.</summary>
internal sealed class MemoryTokenStore(string? token = null) : ITokenStore
{
    private string? token = token;

    public string? Load() => token;

    public void Save(string newToken) => token = newToken;

    public void Clear() => token = null;
}

/// <summary>An action that succeeds or fails as told, counting its runs.</summary>
internal sealed class StubAction(bool succeeds = true, string kind = "stub") : IEventAction
{
    public int Runs { get; private set; }

    public string Kind { get; } = kind;

    public string Describe(IEventActionContext context) => Kind;

    public IReadOnlyList<string> Validate(IEventActionContext context) => [];

    public Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken)
    {
        Runs++;
        return Task.FromResult(succeeds);
    }
}

/// <summary>A context that provides no services.</summary>
internal sealed class EmptyContext : IEventActionContext
{
    public object? GetService(Type serviceType) => null;
}
