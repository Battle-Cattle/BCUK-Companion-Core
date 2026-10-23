using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace BCUKCompanion.TrayApp;

/// <summary>The right-aligned OK/Cancel row that the companion apps' code-built dialogs end with.</summary>
public static class DialogButtons
{
    /// <summary>
    /// Builds the row. OK runs <paramref name="onOk"/>, which validates and sets
    /// <see cref="Window.DialogResult"/> itself. Cancel is <see cref="Button.IsCancel"/>, so
    /// clicking it or pressing Esc closes the modal dialog with <c>DialogResult = false</c>
    /// without a click handler.
    /// </summary>
    /// <param name="onOk">Runs when OK is clicked.</param>
    /// <param name="okText">The OK button's caption.</param>
    /// <returns>A horizontal <see cref="StackPanel"/> holding OK then Cancel.</returns>
    public static StackPanel Create(Action onOk, string okText = "OK")
    {
        ArgumentNullException.ThrowIfNull(onOk);

        var okButton = new Button { Content = okText, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        okButton.Click += (_, _) => onOk();

        var cancelButton = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { okButton, cancelButton },
        };
    }
}
