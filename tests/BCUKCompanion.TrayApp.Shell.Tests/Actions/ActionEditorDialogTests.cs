using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BCUKCompanion.Core.Actions;
using BCUKCompanion.TrayApp.Actions;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Actions;

public sealed class ActionEditorDialogTests
{
    /// <summary>An editor for <see cref="StubAction"/>s whose build result each test sets.</summary>
    private sealed class StubEditor(string name, bool preferred = true) : EventActionEditor
    {
        public Func<(IEventAction? Action, string? Error)> Build { get; set; } = () => (new StubAction(kind: name), null);

        public IEventAction? Loaded { get; private set; }

        public override string DisplayName => name;

        public override UIElement View { get; } = new TextBlock { Text = name };

        public override bool IsPreferredForNewAction => preferred;

        public override bool CanEdit(IEventAction action) => action.Kind == name;

        public override void Load(IEventAction action) => Loaded = action;

        public override IEventAction? TryBuild(out string? error)
        {
            var (action, buildError) = Build();
            error = buildError;
            return action;
        }
    }

    /// <summary>A context under which actions of one kind fail validation.</summary>
    private sealed class RejectingContext(string message) : IEventActionContext
    {
        public string Message { get; } = message;

        public object? GetService(Type serviceType) => serviceType == typeof(RejectingContext) ? this : null;
    }

    /// <summary>An action that's invalid under a <see cref="RejectingContext"/>.</summary>
    private sealed class PickyAction : IEventAction
    {
        public string Kind => "picky";

        public string Describe(IEventActionContext context) => Kind;

        public IReadOnlyList<string> Validate(IEventActionContext context) =>
            context.GetService<RejectingContext>() is { } rejecting ? [rejecting.Message, "second"] : [];

        public Task<bool> ExecuteAsync(IEventActionContext context, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    [SkippableFact]
    public void DelayEditor_BuildsLoadsAndRejects()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var editor = new DelayActionEditor();
            Assert.Equal("Delay", editor.DisplayName);
            Assert.Equal("Delay", editor.ToString());
            Assert.False(editor.IsPreferredForNewAction);
            Assert.True(editor.CanEdit(new DelayAction()));
            Assert.False(editor.CanEdit(new StubAction()));

            Assert.Equal(5, Assert.IsType<DelayAction>(editor.TryBuild(out _)).DelaySeconds);

            editor.Load(new DelayAction { DelaySeconds = 12 });
            Assert.Equal(12.ToString(CultureInfo.CurrentCulture), WpfTest.All<TextBox>(editor.View).Single().Text);
            Assert.Equal(12, Assert.IsType<DelayAction>(editor.TryBuild(out _)).DelaySeconds);

            WpfTest.All<TextBox>(editor.View).Single().Text = "soon";
            Assert.Null(editor.TryBuild(out var error));
            Assert.Equal("Enter a whole number of seconds.", error);
        });
    }

    [SkippableFact]
    public void Dialog_ForANewAction_PicksThePreferredEditorAndReturnsItsAction()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var other = new StubEditor("other", preferred: false);
            var preferred = new StubEditor("preferred");
            var dialog = new EventActionEditDialog([other, preferred], new EmptyContext());
            Assert.Equal("Add Action", dialog.Title);

            var result = WpfTest.ShowDialog(dialog, window =>
            {
                Assert.Equal(Visibility.Collapsed, other.View.Visibility);
                Assert.Equal(Visibility.Visible, preferred.View.Visibility);
                WpfTest.Click(window, "OK");
            });

            Assert.True(result);
            Assert.Equal("preferred", dialog.Result!.Kind);
        });
    }

    [SkippableFact]
    public void Dialog_WithNoPreferredEditor_PicksTheFirst()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var dialog = new EventActionEditDialog([new StubEditor("a", false), new StubEditor("b", false)], new EmptyContext());

            Assert.True(WpfTest.ShowDialog(dialog, window => WpfTest.Click(window, "OK")));
            Assert.Equal("a", dialog.Result!.Kind);
        });
    }

    [SkippableFact]
    public void Dialog_ForAnExistingAction_LoadsItIntoItsEditor()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var first = new StubEditor("first");
            var second = new StubEditor("second");
            var existing = new StubAction(kind: "second");
            var dialog = new EventActionEditDialog([first, second], new EmptyContext(), existing);
            Assert.Equal("Edit Action", dialog.Title);

            WpfTest.ShowDialog(dialog, window => WpfTest.Click(window, "OK"));

            Assert.Same(existing, second.Loaded);
            Assert.Equal("second", dialog.Result!.Kind);
        });
    }

    [SkippableFact]
    public void Dialog_ExplainsWhyItCantBuildOrAccept_AndClearsOnCategoryChange()
    {
        WindowsOnly.Skip();
        WpfTest.Run(() =>
        {
            var editor = new StubEditor("stub");
            var other = new StubEditor("other", preferred: false);
            var dialog = new EventActionEditDialog([editor, other], new RejectingContext("not allowed"));
            var messages = new List<string>();

            var result = WpfTest.ShowDialog(dialog, window =>
            {
                string Error() => WpfTest.Texts(window).Last();
                var combo = WpfTest.All<ComboBox>(window).Single();

                editor.Build = () => (null, "bad input");
                WpfTest.Click(window, "OK");
                messages.Add(Error());

                editor.Build = () => (null, null);
                WpfTest.Click(window, "OK");
                messages.Add(Error());

                editor.Build = () => (new PickyAction(), null);
                WpfTest.Click(window, "OK");
                messages.Add(Error());

                combo.SelectedItem = other;
                messages.Add(Error());

                combo.SelectedItem = null;
                WpfTest.Click(window, "OK");
                messages.Add(Error());
            });

            Assert.NotEqual(true, result);
            Assert.Null(dialog.Result);
            Assert.Equal(["bad input", "Couldn't build the action.", "not allowed\nsecond", "", "Select a category."], messages);
        });
    }
}
