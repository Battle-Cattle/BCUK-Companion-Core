using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using BCUKCompanion.Core;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.Core.Models;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace BCUKCompanion.TrayApp.Actions;

/// <summary>
/// Config shape shared by every integration's settings window: a list of
/// <see cref="EventActionMapping"/>, alongside whatever integration-specific state
/// (devices, ...) the concrete config type adds.
/// </summary>
public interface IEventActionMappingsConfig
{
    List<EventActionMapping> Mappings { get; }
}

/// <summary>
/// Owns the "Event Mappings" tab (reward title -> ordered actions, with Add/Edit/Remove/Test)
/// that every companion app's settings window needs, so each per-user app doesn't have to
/// reimplement this UI for its own action kinds. Lives in <c>BCUKCompanion.TrayApp.Shell</c>
/// (rather than each app's own repo) so a new companion app gets it for free, along with the
/// Add/Edit Action dialog: a subclass only supplies an <see cref="EventActionEditor"/> per
/// integration (<see cref="CreateActionEditors"/>), and window chrome (title, size, any extra
/// tabs/panels).
/// </summary>
public abstract class EventActionMappingsWindow<TConfig> : Window where TConfig : IEventActionMappingsConfig, new()
{
    private readonly EventActionConfigStore<TConfig> configStore;
    private readonly EventActionDispatcher dispatcher;

    /// <summary>
    /// Optional accessor for the shared <see cref="CompanionClient"/>, used to refresh the
    /// reward title suggestions from the live Twitch reward list (GET /api/companion/rewards)
    /// instead of only offering titles already used in saved mappings. A <c>Func</c> rather
    /// than a captured instance because the tray app shell can swap out its
    /// <see cref="CompanionClient"/> (e.g. on a bot-host change) after this window is created.
    /// Null (the default) preserves the old local-only suggestion behavior.
    /// </summary>
    private readonly Func<CompanionClient?>? getCompanionClient;

    protected readonly ObservableCollection<EventActionMapping> Mappings;

    protected readonly ListBox MappingsList = new() { Margin = new Thickness(0, 0, 0, 8), MinHeight = 200 };
    protected readonly ListBox ActionsList = new() { Margin = new Thickness(0, 0, 0, 8), MinHeight = 160 };
    protected readonly ComboBox RewardTitleCombo = new() { IsEditable = true, Margin = new Thickness(0, 0, 0, 8) };

    /// <summary>Whether a new mapping matches a channel-point reward or an advanced command.</summary>
    protected readonly ComboBox TriggerKindCombo = new()
    {
        ItemsSource = Enum.GetValues<EventActionTriggerKind>(),
        SelectedIndex = 0,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private EventActionTriggerKind SelectedKind =>
        TriggerKindCombo.SelectedItem is EventActionTriggerKind kind ? kind : EventActionTriggerKind.Reward;
    protected readonly TextBlock StatusText = new() { Margin = new Thickness(12, 0, 12, 12) };
    protected readonly Button SaveButton = new() { Content = "Save", Width = 90, HorizontalAlignment = HorizontalAlignment.Right };

    protected TConfig InitialConfig { get; }

    private sealed class ActionListItem(IEventAction action, string display)
    {
        public IEventAction Action { get; } = action;

        public override string ToString() => display;
    }

    protected EventActionMappingsWindow(
        EventActionConfigStore<TConfig> configStore,
        TConfig config,
        Func<CompanionClient?>? getCompanionClient = null)
    {
        this.configStore = configStore;
        this.getCompanionClient = getCompanionClient;
        InitialConfig = config;
        Mappings = new ObservableCollection<EventActionMapping>(config.Mappings);
        dispatcher = new EventActionDispatcher(() => BuildConfig().Mappings, BuildContext);

        SaveButton.Click += async (_, _) => await OnSaveAsync().ConfigureAwait(true);
    }

    protected abstract TConfig BuildConfig();

    protected abstract IEventActionContext BuildContext();

    /// <summary>
    /// The categories the Add/Edit Action dialog offers for this app's own action kinds, in
    /// display order; Core's built-in <see cref="DelayActionEditor"/> is appended after them.
    /// Called each time the dialog opens, so return new editors every time. Not needed by a
    /// subclass that overrides <see cref="ShowAddActionDialog"/>/<see cref="ShowEditActionDialog"/>.
    /// </summary>
    protected virtual IEnumerable<EventActionEditor> CreateActionEditors() => [];

    /// <summary>Shows the Add Action dialog; returns the new action, or null if cancelled.</summary>
    protected virtual IEventAction? ShowAddActionDialog() => ShowActionEditDialog(null);

    /// <summary>Shows the Edit Action dialog; returns the edited action, or null if cancelled.</summary>
    protected virtual IEventAction? ShowEditActionDialog(IEventAction existing) => ShowActionEditDialog(existing);

    private IEventAction? ShowActionEditDialog(IEventAction? existing)
    {
        var editors = CreateActionEditors().Append(new DelayActionEditor()).ToList();
        var dialog = new EventActionEditDialog(editors, BuildContext(), existing) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>
    /// The window's bottom bar: <see cref="StatusText"/> with <see cref="SaveButton"/> docked to
    /// its right. Subclasses dock it at the bottom of their own layout.
    /// </summary>
    protected UIElement BuildFooter()
    {
        var footer = new DockPanel { Margin = new Thickness(12, 8, 12, 12) };
        DockPanel.SetDock(SaveButton, Dock.Right);
        footer.Children.Add(SaveButton);
        footer.Children.Add(StatusText);
        return footer;
    }

    protected UIElement BuildMappingsTab()
    {
        MappingsList.ItemsSource = Mappings;
        MappingsList.SelectionChanged += (_, _) => RefreshActionsList();
        TriggerKindCombo.SelectionChanged += (_, _) => RefreshRewardTitleSuggestions();

        var addMappingButton = new Button { Content = "Add Mapping", Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        addMappingButton.Click += (_, _) => OnAddMapping();

        var removeMappingButton = new Button { Content = "Remove Mapping", Width = 110 };
        removeMappingButton.Click += (_, _) => OnRemoveMapping();

        var mappingButtonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Children = { addMappingButton, removeMappingButton },
        };

        var leftPanel = new StackPanel
        {
            Margin = new Thickness(0, 12, 8, 12),
            Width = 220,
            Children =
            {
                new TextBlock { Text = "Trigger" },
                TriggerKindCombo,
                new TextBlock { Text = "Reward title / command name" },
                RewardTitleCombo,
                mappingButtonsPanel,
                new TextBlock { Text = "Mappings" },
                MappingsList,
            },
        };

        var addActionButton = new Button { Content = "Add Action", Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        addActionButton.Click += (_, _) => OnAddAction();

        var editActionButton = new Button { Content = "Edit Action", Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        editActionButton.Click += (_, _) => OnEditAction();

        var removeActionButton = new Button { Content = "Remove Action", Width = 110, Margin = new Thickness(0, 0, 8, 0) };
        removeActionButton.Click += (_, _) => OnRemoveAction();

        var testButton = new Button { Content = "Test", Width = 80 };
        testButton.Click += async (_, _) => await OnTestAsync().ConfigureAwait(true);

        var actionButtonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Children = { addActionButton, editActionButton, removeActionButton, testButton },
        };

        var rightPanel = new StackPanel
        {
            Margin = new Thickness(8, 12, 0, 12),
            Children =
            {
                new TextBlock { Text = "Actions for selected mapping" },
                ActionsList,
                actionButtonsPanel,
            },
        };

        var splitPanel = new DockPanel();
        DockPanel.SetDock(leftPanel, Dock.Left);
        splitPanel.Children.Add(leftPanel);
        splitPanel.Children.Add(rightPanel);

        return splitPanel;
    }

    // Bumped on every RefreshRewardTitleSuggestions() call so an in-flight server fetch can
    // tell it's been superseded and skip applying its (possibly stale) result out of order.
    private int _rewardTitleRefreshSequence;

    protected void RefreshRewardTitleSuggestions()
    {
        // Increment unconditionally, even when there's no logged-in client below: otherwise a
        // refresh that finds no client doesn't invalidate an earlier in-flight fetch, which can
        // then still pass the sequence check and overwrite these (correct) local-only
        // suggestions with its now-stale server result.
        int sequence = ++_rewardTitleRefreshSequence;
        var kind = SelectedKind;
        RewardTitleCombo.ItemsSource = MappedNames(kind).ToList();

        // Best-effort enhancement: if we have a logged-in companion client, replace the
        // local-only suggestion list above with the live reward (or command) catalog once it arrives.
        if (getCompanionClient?.Invoke() is { IsLoggedIn: true } client)
        {
            _ = RefreshRewardTitleSuggestionsFromServerAsync(client, kind, sequence);
        }
    }

    /// <summary>The names already used by mappings of <paramref name="kind"/>.</summary>
    private IEnumerable<string> MappedNames(EventActionTriggerKind kind) =>
        Mappings.Where(m => m.TriggerKind == kind).Select(m => m.RewardTitle).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fetches the live names for <paramref name="kind"/>: enabled reward titles, or the
    /// advanced commands that send events to this app.
    /// </summary>
    private static async Task<IEnumerable<string>> FetchNamesAsync(CompanionClient client, EventActionTriggerKind kind)
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
    /// Replaces the title suggestions with the server's rewards or commands (per <paramref name="kind"/>) plus the
    /// already-mapped names, unless the fetch fails or a newer refresh (<paramref name="sequence"/>) has started.
    /// </summary>
    private async Task RefreshRewardTitleSuggestionsFromServerAsync(CompanionClient client, EventActionTriggerKind kind, int sequence)
    {
        IEnumerable<string> names;
        try
        {
            names = await FetchNamesAsync(client, kind).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Server unreachable, token expired, etc. — the local-only suggestions set in
            // RefreshRewardTitleSuggestions() above stand; there's no dedicated retry here
            // since the user can always type a title that isn't in the list.
            return;
        }

        if (sequence != _rewardTitleRefreshSequence)
        {
            // A newer refresh was started while this fetch was in flight — its result will
            // apply instead, so don't overwrite it with this now-stale one.
            return;
        }

        var titles = names
            .Concat(MappedNames(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Preserve whatever the user has already typed: RewardTitleCombo is editable, and WPF
        // can clear its Text when a fresh ItemsSource assignment invalidates the current
        // selection, which would silently drop a title typed while this async fetch was in
        // flight.
        var typedTitle = RewardTitleCombo.Text;
        RewardTitleCombo.ItemsSource = titles;
        RewardTitleCombo.Text = typedTitle;
    }

    protected void RefreshActionsList()
    {
        var mapping = MappingsList.SelectedItem as EventActionMapping;
        if (mapping is null)
        {
            ActionsList.ItemsSource = null;
            return;
        }

        var context = BuildContext();
        ActionsList.ItemsSource = mapping.Actions
            .Select(a => new ActionListItem(a, a.Describe(context)))
            .ToList();
    }

    private void OnAddMapping()
    {
        var title = RewardTitleCombo.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            StatusText.Text = "Enter a reward title or command name first.";
            return;
        }

        var kind = SelectedKind;
        var existing = Mappings.FirstOrDefault(m =>
            m.TriggerKind == kind && string.Equals(m.RewardTitle, title, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            MappingsList.SelectedItem = existing;
            return;
        }

        var mapping = new EventActionMapping(title, [], kind);
        Mappings.Add(mapping);
        MappingsList.SelectedItem = mapping;
        RefreshRewardTitleSuggestions();
    }

    private void OnRemoveMapping()
    {
        if (MappingsList.SelectedItem is not EventActionMapping selected)
        {
            StatusText.Text = "Select a mapping to remove.";
            return;
        }

        Mappings.Remove(selected);
        RefreshRewardTitleSuggestions();
        RefreshActionsList();
    }

    private void OnAddAction()
    {
        if (MappingsList.SelectedItem is not EventActionMapping mapping)
        {
            StatusText.Text = "Select a mapping first.";
            return;
        }

        if (ShowAddActionDialog() is { } action)
        {
            ReplaceMappingActions(mapping, [.. mapping.Actions, action]);
            RefreshActionsList();
        }
    }

    private void OnEditAction()
    {
        if (MappingsList.SelectedItem is not EventActionMapping mapping)
        {
            StatusText.Text = "Select a mapping first.";
            return;
        }

        if (ActionsList.SelectedItem is not ActionListItem selected)
        {
            StatusText.Text = "Select an action to edit.";
            return;
        }

        var index = ActionsList.SelectedIndex;
        if (ShowEditActionDialog(selected.Action) is { } updated)
        {
            var newActions = mapping.Actions.ToList();
            newActions[index] = updated;
            ReplaceMappingActions(mapping, newActions);
            RefreshActionsList();
        }
    }

    private void OnRemoveAction()
    {
        if (MappingsList.SelectedItem is not EventActionMapping mapping)
        {
            StatusText.Text = "Select a mapping first.";
            return;
        }

        if (ActionsList.SelectedItem is not ActionListItem selected)
        {
            StatusText.Text = "Select an action to remove.";
            return;
        }

        var newActions = mapping.Actions.ToList();
        newActions.RemoveAt(ActionsList.SelectedIndex);
        ReplaceMappingActions(mapping, newActions);
        RefreshActionsList();
    }

    private async Task OnTestAsync()
    {
        if (MappingsList.SelectedItem is not EventActionMapping mapping)
        {
            StatusText.Text = "Select a mapping to test.";
            return;
        }

        StatusText.Text = "Testing...";

        EventDispatchResult result;
        string report;
        try
        {
            // Guards DispatchAsync, BuildContext(), and Describe() alike: this runs off an
            // async void button-click handler, so any exception escaping here (BuildConfig()/
            // BuildContext() run outside EventActionDispatcher's own per-action try/catch, and
            // a subclass's Describe() override is equally unguarded) would otherwise crash the
            // process instead of just failing this one test run.
            result = await dispatcher.DispatchAsync(mapping.RewardTitle, mapping.TriggerKind).ConfigureAwait(true);

            if (result.ActionResults.Count == 0)
            {
                StatusText.Text = "Test did not dispatch any actions.";
                return;
            }

            var context = BuildContext();
            report = string.Join("\n", result.ActionResults.Select(r =>
                $"{r.Action.Describe(context)}: {(r.Success ? "OK" : r.ErrorMessage ?? "Failed")}"));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Test failed: {ex.Message}";
            return;
        }

        MessageBox.Show(this, report, $"Test results: {result.RewardTitle}");
        StatusText.Text = result.AllSucceeded ? "Test succeeded." : "Test completed with errors.";
    }

    protected void ReplaceMappingActions(EventActionMapping mapping, List<IEventAction> newActions)
    {
        var idx = Mappings.IndexOf(mapping);
        if (idx < 0) return;
        // Captured before the swap: WPF's ListBox drops the old item from its selection
        // synchronously on the Replace notification Mappings[idx] = updated raises below, so
        // MappingsList.SelectedItem no longer references `mapping` by the time this checks it.
        var wasSelected = ReferenceEquals(MappingsList.SelectedItem, mapping);
        var updated = mapping with { Actions = newActions };
        Mappings[idx] = updated;
        if (wasSelected)
            MappingsList.SelectedItem = updated;
    }

    /// <summary>
    /// Removes every action matching <paramref name="shouldRemove"/> from every mapping — e.g. the
    /// actions targeting a device that was just deleted — and returns how many were removed.
    /// </summary>
    protected int RemoveActions(Func<IEventAction, bool> shouldRemove)
    {
        var removedCount = 0;
        foreach (var mapping in Mappings.ToList())
        {
            var remaining = mapping.Actions.Where(a => !shouldRemove(a)).ToList();
            if (remaining.Count != mapping.Actions.Count)
            {
                removedCount += mapping.Actions.Count - remaining.Count;
                ReplaceMappingActions(mapping, remaining);
            }
        }

        if (removedCount > 0)
        {
            RefreshActionsList();
        }
        return removedCount;
    }

    private async Task OnSaveAsync()
    {
        // Disable the whole window, not just SaveButton: BuildConfig() below snapshots the
        // current mappings/devices, and configStore.Save writes that snapshot atomically. If
        // any editable control stayed live during the save, an edit made mid-save wouldn't be
        // in the snapshot, yet "Saved to ..." would still claim it was persisted.
        IsEnabled = false;
        StatusText.Text = "Saving...";
        try
        {
            var config = BuildConfig();
            await Task.Run(() => configStore.Save(config)).ConfigureAwait(true);
            StatusText.Text = $"Saved to {configStore.ConfigFilePath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
