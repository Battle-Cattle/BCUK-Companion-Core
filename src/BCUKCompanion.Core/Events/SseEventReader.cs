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

    /// <summary>Creates a parser over <paramref name="reader"/>, which it reads line by line.</summary>
    /// <param name="reader">The response stream's text.</param>
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
    /// <param name="onActivity">Called for every line read, including comments and blank lines; null for none.</param>
    /// <param name="cancellationToken">Stops reading; passed to each line read.</param>
    /// <returns>The events, in order, until the reader reaches the end of the stream (a final unterminated block is still yielded).</returns>
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

        if (dataLines is { Count: > 0 })
        {
            yield return new SseEvent { EventName = eventName, Data = string.Join('\n', dataLines) };
        }
    }

    /// <summary>Returns a field's value: the line after <paramref name="prefix"/>, minus one leading space if present (per the SSE spec).</summary>
    /// <param name="line">The whole SSE line, which starts with <paramref name="prefix"/>.</param>
    /// <param name="prefix">The field name and colon, e.g. <c>data:</c>.</param>
    /// <returns>The field's value.</returns>
    private static string StripFieldPrefix(string line, string prefix)
    {
        string rest = line[prefix.Length..];
        return rest.StartsWith(' ') ? rest[1..] : rest;
    }
}
