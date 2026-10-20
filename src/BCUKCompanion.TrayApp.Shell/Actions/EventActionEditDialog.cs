using System.Windows;
using System.Windows.Controls;
using BCUKCompanion.Core.Actions;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace BCUKCompanion.TrayApp.Actions;

/// <summary>
/// The Add/Edit Action dialog every mappings window shares: a "Category" picker over the
/// app's <see cref="EventActionEditor"/>s, showing the selected one's controls, so one
/// <see cref="EventActionMapping"/> can mix action kinds from different integrations.
/// </summary>
public sealed class EventActionEditDialog : Window
{
    private readonly IReadOnlyList<EventActionEditor> editors;
    private readonly IEventActionContext validationContext;
    private readonly ComboBox categoryCombo = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock errorText = new() { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    /// <param name="editors">The categories to offer, in display order.</param>
    /// <param name="validationContext">Passed to <see cref="IEventAction.Validate"/> before accepting the action.</param>
    /// <param name="existing">The action to edit, or null to add one.</param>
    public EventActionEditDialog(
        IReadOnlyList<EventActionEditor> editors, IEventActionContext validationContext, IEventAction? existing = null)
    {
        this.editors = editors;
        this.validationContext = validationContext;

        Title = existing is null ? "Add Action" : "Edit Action";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        categoryCombo.ItemsSource = editors;
        categoryCombo.SelectionChanged += (_, _) => RefreshVisibility();

        var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        okButton.Click += (_, _) => OnOk();

        var cancelButton = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        cancelButton.Click += (_, _) => DialogResult = false;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "Category" });
        panel.Children.Add(categoryCombo);
        foreach (var editor in editors)
        {
            panel.Children.Add(editor.View);
        }
        panel.Children.Add(errorText);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { okButton, cancelButton },
        });
        Content = panel;

        var existingEditor = existing is null ? null : editors.FirstOrDefault(e => e.CanEdit(existing));
        if (existingEditor is not null)
        {
            existingEditor.Load(existing!);
            categoryCombo.SelectedItem = existingEditor;
        }
        else
        {
            categoryCombo.SelectedItem = editors.FirstOrDefault(e => e.IsPreferredForNewAction) ?? editors.FirstOrDefault();
        }
        RefreshVisibility();
    }

    /// <summary>The built action once the dialog closes with OK.</summary>
    public IEventAction? Result { get; private set; }

    /// <summary>Shows only the selected category's editor, and clears any previous error.</summary>
    private void RefreshVisibility()
    {
        foreach (var editor in editors)
        {
            editor.View.Visibility = ReferenceEquals(editor, categoryCombo.SelectedItem) ? Visibility.Visible : Visibility.Collapsed;
        }
        errorText.Text = string.Empty;
    }

    /// <summary>Builds and validates the action from the selected editor, closing with it in <see cref="Result"/>, or shows why it can't be used.</summary>
    private void OnOk()
    {
        if (categoryCombo.SelectedItem is not EventActionEditor editor)
        {
            errorText.Text = "Select a category.";
            return;
        }

        var action = editor.TryBuild(out var error);
        if (action is null)
        {
            errorText.Text = error ?? "Couldn't build the action.";
            return;
        }

        var errors = action.Validate(validationContext);
        if (errors.Count > 0)
        {
            errorText.Text = string.Join("\n", errors);
            return;
        }

        Result = action;
        DialogResult = true;
    }
}
