using System.Runtime.CompilerServices;

namespace BCUKCompanion.Core.Events;

/// <summary>
/// Minimal Server-Sent Events line parser. Understands "data:" and
/// "event:" fields and ignores comment lines (starting with ':', used by
/// the companion endpoint as a keepalive) and any other field types.
/// </summary>
public sealed class SseEventReader
{
    private readonly TextReader _reader;

    public SseEventReader(TextReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// Yields one <see cref="SseEvent"/> per blank-line-terminated block
    /// that contained at least one "data:" line. <paramref name="onActivity"/>,
    /// if given, is invoked for every line read (including comments) so a
    /// caller can use it as a connection liveness signal.
    /// </summary>
    public async IAsyncEnumerable<SseEvent> ReadEventsAsync(
        Action? onActivity = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? eventName = null;
        List<string>? dataLines = null;

        string? line;
        while ((line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            onActivity?.Invoke();

            if (line.Length == 0)
            {
                if (dataLines is { Count: > 0 })
                {
                    yield return new SseEvent { EventName = eventName, Data = string.Join('\n', dataLines) };
                }

                eventName = null;
                dataLines = null;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines ??= new List<string>();
                dataLines.Add(StripFieldPrefix(line, "data:"));
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = StripFieldPrefix(line, "event:");
            }
            // id:, retry:, and any other field types are ignored — unused by this endpoint.
        }
    }

    private static string StripFieldPrefix(string line, string prefix)
    {
        string rest = line[prefix.Length..];
        return rest.StartsWith(' ') ? rest[1..] : rest;
    }
}
