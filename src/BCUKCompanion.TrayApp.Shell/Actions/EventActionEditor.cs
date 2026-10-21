using System.Windows;
using BCUKCompanion.Core.Actions;

namespace BCUKCompanion.TrayApp.Actions;

/// <summary>
/// One category in the shared Add/Edit Action dialog (see
/// <see cref="EventActionMappingsWindow{TConfig}.CreateActionEditors"/>): the parameter controls
/// for one integration's action kinds, and the logic to turn them into an
/// <see cref="IEventAction"/>. The dialog creates a fresh set of editors each time it opens, so
/// an editor owns its controls outright. The dialog runs <see cref="IEventAction.Validate"/> on
/// whatever <see cref="TryBuild"/> returns, so editors only need to report input they can't parse.
/// </summary>
public abstract class EventActionEditor
{
    /// <summary>The category's name in the dialog's category picker, e.g. "Delay".</summary>
    public abstract string DisplayName { get; }

    /// <summary>The category's parameter controls, shown while it's selected.</summary>
    public abstract UIElement View { get; }

    /// <summary>
    /// Whether the Add dialog may preselect this category — false when it has nothing to act on
    /// yet (e.g. no devices configured). The first preferred category is preselected.
    /// </summary>
    public virtual bool IsPreferredForNewAction => true;

    /// <summary>Whether this category edits <paramref name="action"/>'s kind.</summary>
    /// <param name="action">An existing action the dialog is about to edit.</param>
    /// <returns>True when this editor handles that action's kind; otherwise false.</returns>
    public abstract bool CanEdit(IEventAction action);

    /// <summary>Fills the controls from an existing action that <see cref="CanEdit"/> accepted.</summary>
    /// <param name="action">The action whose parameters the controls should show.</param>
    public abstract void Load(IEventAction action);

    /// <summary>
    /// Builds the action from the controls, or returns null with <paramref name="error"/> set when
    /// the input can't be parsed.
    /// </summary>
    /// <param name="error">A message for the user saying why the input can't be used, or null on success.</param>
    /// <returns>The action built from the controls, or null when the input can't be parsed.</returns>
    public abstract IEventAction? TryBuild(out string? error);

    /// <summary>The category name, as the dialog's picker shows it.</summary>
    /// <returns>The editor's <see cref="DisplayName"/>.</returns>
    public override string ToString() => DisplayName;
}
