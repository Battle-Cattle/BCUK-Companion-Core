using System.Collections.Concurrent;
using System.Net;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests;

/// <summary>
/// Tests that change the Shell's process-wide state (AppPaths' folder, AutoStartService's
/// registry value name) run one at a time in this collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedShellState
{
    public const string Name = "Shared Shell state";
}

/// <summary>Answers each request with whatever <see cref="Respond"/> returns for its path, recording the paths.</summary>
internal sealed class RouteHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

    public ConcurrentQueue<string> Paths { get; } = new();

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Enqueue(request.RequestUri!.AbsolutePath);
        // Off the caller's thread, so a response that waits can't block a UI thread awaiting it.
        return Task.Run(() => Respond(request), cancellationToken);
    }
}

/// <summary>A data folder of its own for AppPaths (and AutoStartService's value name), deleted on dispose.</summary>
internal sealed class TempAppFolder : IDisposable
{
    public TempAppFolder()
    {
        AppPaths.Configure(Name);
        BCUKCompanion.TrayApp.Services.AutoStartService.Configure(Name);
    }

    public string Name { get; } = $"BCUKCompanion.TrayApp.Shell.Tests.{Guid.NewGuid():N}";

    public void Dispose()
    {
        foreach (var file in new[] { AppPaths.SettingsFile, AppPaths.TokenFile })
        {
            var directory = Path.GetDirectoryName(file)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            BCUKCompanion.TrayApp.Services.AutoStartService.SetEnabled(false);
        }
    }
}
