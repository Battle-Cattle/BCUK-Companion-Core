using BCUKCompanion.Core;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Models;

namespace BCUKCompanion.TrayApp.Actions;

/// <summary>
/// Works out the reward title / command name suggestions <see cref="EventActionMappingsWindow{TConfig}"/>
/// offers: the names already mapped straight away, then the bot's live rewards or commands once
/// they arrive, dropping a fetch that a newer refresh has superseded. Kept free of WPF so it can be
/// tested; the window only shows the lists it's handed.
/// </summary>
internal sealed class MappingNameSuggestions
{
    private readonly EventActionMappingsEditor _editor;
    private readonly Func<CompanionClient?>? _getCompanionClient;

    // Bumped on every Refresh() call so an in-flight server fetch can tell it's been superseded
    // and skip applying its (possibly stale) result out of order.
    private int _sequence;

    /// <summary>Creates the suggestion source over the mappings in <paramref name="editor"/>.</summary>
    /// <param name="editor">The mappings being edited, whose names are always suggested.</param>
    /// <param name="getCompanionClient">Returns the current client, for live suggestions; null for local suggestions only.</param>
    public MappingNameSuggestions(EventActionMappingsEditor editor, Func<CompanionClient?>? getCompanionClient)
    {
        _editor = editor;
        _getCompanionClient = getCompanionClient;
    }

    /// <summary>
    /// Hands <paramref name="showMappedNames"/> the names already mapped for <paramref name="kind"/>
    /// straight away, then, when a logged-in client is available, fetches the live names and hands
    /// <paramref name="showLiveNames"/> them merged with the mapped ones — unless the fetch fails or
    /// a later refresh has started by the time it finishes.
    /// </summary>
    /// <param name="kind">Whether to suggest reward titles or command names.</param>
    /// <param name="showMappedNames">Shows the local-only suggestion list.</param>
    /// <param name="showLiveNames">Shows the merged live suggestion list; called on the thread that called this method when it has a synchronization context.</param>
    /// <returns>The live fetch, which never faults, or null when there's no logged-in client to fetch with.</returns>
    public Task? Refresh(EventActionTriggerKind kind, Action<List<string>> showMappedNames, Action<List<string>> showLiveNames)
    {
        // Increment unconditionally, even when there's no logged-in client below: otherwise a
        // refresh that finds no client doesn't invalidate an earlier in-flight fetch, which can
        // then still pass the sequence check and overwrite these (correct) local-only
        // suggestions with its now-stale server result.
        int sequence = ++_sequence;
        showMappedNames(_editor.MappedNames(kind).ToList());

        // Best-effort enhancement: if we have a logged-in companion client, replace the
        // local-only suggestion list above with the live reward (or command) catalog once it arrives.
        return _getCompanionClient?.Invoke() is { IsLoggedIn: true } client
            ? ShowLiveNamesAsync(client, kind, sequence, showLiveNames)
            : null;
    }

    /// <summary>
    /// Fetches the live names for <paramref name="kind"/>: enabled reward titles, or the
    /// advanced commands that send events to this app.
    /// </summary>
    /// <param name="client">The logged-in client to query the bot with.</param>
    /// <param name="kind">Whether to fetch reward titles or command names.</param>
    /// <returns>A task that completes with the names; it faults if the bot can't be reached.</returns>
    internal static async Task<IEnumerable<string>> FetchNamesAsync(CompanionClient client, EventActionTriggerKind kind)
    {
        if (kind == EventActionTriggerKind.Command)
        {
            IReadOnlyList<AdvancedCommand> commands = await client.GetAdvancedCommandsAsync().ConfigureAwait(true);
            return commands.Where(c => c.CompanionEnabled).Select(c => c.Name);
        }

        IReadOnlyList<Reward> rewards = await client.GetRewardsAsync().ConfigureAwait(true);
        return rewards.Where(r => r.IsEnabled).Select(r => r.Title);
    }

    /// <summary>
    /// Shows the server's rewards or commands (per <paramref name="kind"/>) plus the already-mapped
    /// names, unless the fetch fails or a newer refresh (<paramref name="sequence"/>) has started.
    /// </summary>
    /// <param name="client">The logged-in client to query the bot with.</param>
    /// <param name="kind">Whether to fetch reward titles or command names.</param>
    /// <param name="sequence">The refresh number this fetch belongs to; its result is dropped if a later refresh has started.</param>
    /// <param name="showLiveNames">Shows the merged suggestion list.</param>
    /// <returns>A task that completes once the suggestions are shown or skipped; it never faults.</returns>
    private async Task ShowLiveNamesAsync(CompanionClient client, EventActionTriggerKind kind, int sequence, Action<List<string>> showLiveNames)
    {
        IEnumerable<string> names;
        try
        {
            names = await FetchNamesAsync(client, kind).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Server unreachable, token expired, etc. — the local-only suggestions shown by
            // Refresh() stand; there's no dedicated retry here since the user can always type
            // a title that isn't in the list.
            return;
        }

        if (sequence != _sequence)
        {
            // A newer refresh was started while this fetch was in flight — its result will
            // apply instead, so don't overwrite it with this now-stale one.
            return;
        }

        showLiveNames(_editor.MergeSuggestions(names, kind));
    }
}
