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

    /// <summary>Creates the editor with a default delay of 5 seconds.</summary>
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

    /// <summary>The category name in the dialog's picker.</summary>
    public override string DisplayName => "Delay";

    /// <summary>The delay text box and its label.</summary>
    public override UIElement View { get; }

    /// <summary>Only useful between other actions, so never the default for a new action.</summary>
    public override bool IsPreferredForNewAction => false;

    /// <summary>True for <see cref="DelayAction"/>.</summary>
    /// <param name="action">The action being edited.</param>
    /// <returns>True when <paramref name="action"/> is a <see cref="DelayAction"/>; otherwise false.</returns>
    public override bool CanEdit(IEventAction action) => action is DelayAction;

    /// <summary>Shows an existing action's delay.</summary>
    /// <param name="action">A <see cref="DelayAction"/>.</param>
    public override void Load(IEventAction action) =>
        delayBox.Text = ((DelayAction)action).DelaySeconds.ToString(CultureInfo.CurrentCulture);

    /// <summary>Builds a Delay action from the text box, which must hold a whole number (its range is checked by <see cref="DelayAction.Validate"/>).</summary>
    /// <param name="error">Why the input can't be used, when null is returned.</param>
    /// <returns>The new <see cref="DelayAction"/>, or null when the text isn't a whole number.</returns>
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
