using System.Collections.ObjectModel;
using BCUKCompanion.Core.Actions;

namespace BCUKCompanion.TrayApp.Actions;

/// <summary>
/// The working list of mappings behind <see cref="EventActionMappingsWindow{TConfig}"/>: adding,
/// finding and removing mappings, and changing a mapping's actions. Kept free of WPF so it can be
/// tested; the window only binds to <see cref="Mappings"/> and keeps its selection in step.
/// </summary>
public sealed class EventActionMappingsEditor
{
    /// <summary>Starts editing <paramref name="mappings"/>, copied into <see cref="Mappings"/>.</summary>
    /// <param name="mappings">The mappings as loaded.</param>
    public EventActionMappingsEditor(IEnumerable<EventActionMapping> mappings)
    {
        Mappings = new ObservableCollection<EventActionMapping>(mappings);
    }

    /// <summary>The mappings being edited, in display order. Mappings are immutable, so a change replaces the entry.</summary>
    public ObservableCollection<EventActionMapping> Mappings { get; }

    /// <summary>
    /// Finds the mapping for <paramref name="name"/> and <paramref name="kind"/> (ignoring case),
    /// or adds an empty one at the end.
    /// </summary>
    /// <param name="name">The reward title or command name; surrounding spaces are trimmed.</param>
    /// <param name="kind">Whether the mapping matches a reward or an advanced command.</param>
    /// <returns>The existing or new mapping, or null when <paramref name="name"/> is blank.</returns>
    public EventActionMapping? FindOrAdd(string name, EventActionTriggerKind kind)
    {
        var title = name.Trim();
        if (title.Length == 0)
        {
            return null;
        }

        var existing = Mappings.FirstOrDefault(m =>
            m.TriggerKind == kind && string.Equals(m.RewardTitle, title, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var mapping = new EventActionMapping(title, [], kind);
        Mappings.Add(mapping);
        return mapping;
    }

    /// <summary>Removes <paramref name="mapping"/>.</summary>
    /// <param name="mapping">The mapping to remove.</param>
    /// <returns>True if it was in the list.</returns>
    public bool Remove(EventActionMapping mapping) => Mappings.Remove(mapping);

    /// <summary>The names already used by mappings of <paramref name="kind"/>.</summary>
    /// <param name="kind">Whether to list reward titles or command names.</param>
    /// <returns>The distinct names (case-insensitively), in mapping order.</returns>
    public IEnumerable<string> MappedNames(EventActionTriggerKind kind) =>
        Mappings.Where(m => m.TriggerKind == kind).Select(m => m.RewardTitle).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The title suggestions to offer: <paramref name="liveNames"/> plus the names already mapped for <paramref name="kind"/>.</summary>
    /// <param name="liveNames">The bot's current reward titles or command names.</param>
    /// <param name="kind">Whether the names are reward titles or command names.</param>
    /// <returns>The distinct names (case-insensitively), sorted case-insensitively.</returns>
    public List<string> MergeSuggestions(IEnumerable<string> liveNames, EventActionTriggerKind kind) =>
        liveNames
            .Concat(MappedNames(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Replaces <paramref name="mapping"/>'s actions, in place in <see cref="Mappings"/>.</summary>
    /// <param name="mapping">The mapping to update.</param>
    /// <param name="newActions">Its new actions, in run order.</param>
    /// <returns>The updated mapping, or null if <paramref name="mapping"/> is no longer in the list.</returns>
    public EventActionMapping? ReplaceActions(EventActionMapping mapping, List<IEventAction> newActions)
    {
        var index = Mappings.IndexOf(mapping);
        if (index < 0)
        {
            return null;
        }

        var updated = mapping with { Actions = newActions };
        Mappings[index] = updated;
        return updated;
    }

    /// <summary>Adds <paramref name="action"/> to the end of <paramref name="mapping"/>'s actions.</summary>
    /// <param name="mapping">The mapping to update.</param>
    /// <param name="action">The action to append.</param>
    /// <returns>The updated mapping, or null if <paramref name="mapping"/> is no longer in the list.</returns>
    public EventActionMapping? AppendAction(EventActionMapping mapping, IEventAction action) =>
        ReplaceActions(mapping, [.. mapping.Actions, action]);

    /// <summary>Replaces the action at <paramref name="index"/> in <paramref name="mapping"/>.</summary>
    /// <param name="mapping">The mapping to update.</param>
    /// <param name="index">The zero-based position of the action to replace.</param>
    /// <param name="action">The replacement action.</param>
    /// <returns>The updated mapping, or null if <paramref name="mapping"/> is no longer in the list.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> isn't a position in the mapping's actions.</exception>
    public EventActionMapping? ReplaceActionAt(EventActionMapping mapping, int index, IEventAction action)
    {
        var newActions = mapping.Actions.ToList();
        newActions[index] = action;
        return ReplaceActions(mapping, newActions);
    }

    /// <summary>Removes the action at <paramref name="index"/> from <paramref name="mapping"/>.</summary>
    /// <param name="mapping">The mapping to update.</param>
    /// <param name="index">The zero-based position of the action to remove.</param>
    /// <returns>The updated mapping, or null if <paramref name="mapping"/> is no longer in the list.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> isn't a position in the mapping's actions.</exception>
    public EventActionMapping? RemoveActionAt(EventActionMapping mapping, int index)
    {
        var newActions = mapping.Actions.ToList();
        newActions.RemoveAt(index);
        return ReplaceActions(mapping, newActions);
    }

    /// <summary>
    /// Removes every action matching <paramref name="shouldRemove"/> from every mapping — e.g. the
    /// actions targeting a device that was just deleted. Mappings with nothing removed are left as they are.
    /// </summary>
    /// <param name="shouldRemove">Returns true for each action to remove.</param>
    /// <returns>The number of actions removed across all mappings; 0 if none matched.</returns>
    public int RemoveActions(Func<IEventAction, bool> shouldRemove)
    {
        var removedCount = 0;
        foreach (var mapping in Mappings.ToList())
        {
            var remaining = mapping.Actions.Where(a => !shouldRemove(a)).ToList();
            if (remaining.Count != mapping.Actions.Count)
            {
                removedCount += mapping.Actions.Count - remaining.Count;
                ReplaceActions(mapping, remaining);
            }
        }

        return removedCount;
    }
}
