using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BCUKCompanion.Core.Actions;
using TextBox = System.Windows.Controls.TextBox;

namespace BCUKCompanion.TrayApp.Actions;

/// <summary>Editor for Core's built-in <see cref="DelayAction"/>, offered by every mappings window.</summary>
public sealed class DelayActionEditor : EventActionEditor
{
    private readonly TextBox delayBox = new() { Text = "5" };

    public DelayActionEditor()
    {
        View = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new TextBlock { Text = $"Delay (seconds, {DelayAction.MinDelaySeconds}-{DelayAction.MaxDelaySeconds})" },
                delayBox,
            },
        };
    }

    public override string DisplayName => "Delay";

    public override UIElement View { get; }

    // Only useful between other actions, so never the default for a new action.
    public override bool IsPreferredForNewAction => false;

    public override bool CanEdit(IEventAction action) => action is DelayAction;

    public override void Load(IEventAction action) =>
        delayBox.Text = ((DelayAction)action).DelaySeconds.ToString(CultureInfo.CurrentCulture);

    public override IEventAction? TryBuild(out string? error)
    {
        if (!int.TryParse(delayBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds))
        {
            error = "Enter a whole number of seconds.";
            return null;
        }

        error = null;
        return new DelayAction { DelaySeconds = seconds };
    }
}
