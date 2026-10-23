using BCUKCompanion.Core.Actions;
using BCUKCompanion.TrayApp.Actions;
using Xunit;

namespace BCUKCompanion.TrayApp.Tests.Actions;

public class EventActionMappingsEditorTests
{
    private static EventActionMapping Mapping(string title, EventActionTriggerKind kind = EventActionTriggerKind.Reward, params IEventAction[] actions) =>
        new(title, actions, kind);

    [Fact]
    public void FindOrAdd_AddsATrimmedEmptyMappingAtTheEnd()
    {
        var editor = new EventActionMappingsEditor([Mapping("Hydrate")]);

        var added = editor.FindOrAdd("  Stretch  ", EventActionTriggerKind.Reward);

        Assert.NotNull(added);
        Assert.Equal("Stretch", added.RewardTitle);
        Assert.Empty(added.Actions);
        Assert.Same(added, editor.Mappings[1]);
    }

    [Fact]
    public void FindOrAdd_ReturnsTheExistingMappingIgnoringCase_WithoutAddingOne()
    {
        var existing = Mapping("Hydrate");
        var editor = new EventActionMappingsEditor([existing]);

        Assert.Same(existing, editor.FindOrAdd("hydrate", EventActionTriggerKind.Reward));
        Assert.Single(editor.Mappings);
    }

    [Fact]
    public void FindOrAdd_TreatsTheSameNameWithAnotherTriggerKindAsANewMapping()
    {
        var editor = new EventActionMappingsEditor([Mapping("hug")]);

        var command = editor.FindOrAdd("hug", EventActionTriggerKind.Command);

        Assert.Equal(EventActionTriggerKind.Command, command!.TriggerKind);
        Assert.Equal(2, editor.Mappings.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FindOrAdd_RejectsABlankName(string name)
    {
        var editor = new EventActionMappingsEditor([]);

        Assert.Null(editor.FindOrAdd(name, EventActionTriggerKind.Reward));
        Assert.Empty(editor.Mappings);
    }

    [Fact]
    public void Remove_RemovesTheMapping()
    {
        var mapping = Mapping("Hydrate");
        var editor = new EventActionMappingsEditor([mapping]);

        Assert.True(editor.Remove(mapping));
        Assert.Empty(editor.Mappings);
    }

    [Fact]
    public void MappedNames_ListsDistinctNamesOfOneKind()
    {
        var editor = new EventActionMappingsEditor(
            [Mapping("Hydrate"), Mapping("hydrate"), Mapping("hug", EventActionTriggerKind.Command), Mapping("Stretch")]);

        Assert.Equal(["Hydrate", "Stretch"], editor.MappedNames(EventActionTriggerKind.Reward));
        Assert.Equal(["hug"], editor.MappedNames(EventActionTriggerKind.Command));
    }

    [Fact]
    public void MergeSuggestions_CombinesLiveAndMappedNames_SortedWithoutDuplicates()
    {
        var editor = new EventActionMappingsEditor([Mapping("Old reward"), Mapping("hydrate")]);

        var titles = editor.MergeSuggestions(["Hydrate", "Bonk"], EventActionTriggerKind.Reward);

        Assert.Equal(["Bonk", "Hydrate", "Old reward"], titles);
    }

    [Fact]
    public void AppendAction_AddsToTheEndAndReplacesTheMappingInPlace()
    {
        var first = new StubAction(kind: "first");
        var mapping = Mapping("Hydrate", EventActionTriggerKind.Reward, first);
        var editor = new EventActionMappingsEditor([Mapping("Other"), mapping]);
        var second = new StubAction(kind: "second");

        var updated = editor.AppendAction(mapping, second);

        Assert.NotNull(updated);
        Assert.Equal([first, second], updated.Actions);
        Assert.Same(updated, editor.Mappings[1]);
    }

    [Fact]
    public void ReplaceActionAt_ReplacesOnlyThatAction()
    {
        var a = new StubAction(kind: "a");
        var b = new StubAction(kind: "b");
        var mapping = Mapping("Hydrate", EventActionTriggerKind.Reward, a, b);
        var editor = new EventActionMappingsEditor([mapping]);
        var c = new StubAction(kind: "c");

        var updated = editor.ReplaceActionAt(mapping, 1, c);

        Assert.Equal([a, c], updated!.Actions);
    }

    [Fact]
    public void RemoveActionAt_RemovesOnlyThatAction()
    {
        var a = new StubAction(kind: "a");
        var b = new StubAction(kind: "b");
        var mapping = Mapping("Hydrate", EventActionTriggerKind.Reward, a, b);
        var editor = new EventActionMappingsEditor([mapping]);

        var updated = editor.RemoveActionAt(mapping, 0);

        Assert.Equal([b], updated!.Actions);
    }

    [Fact]
    public void ActionChanges_ToAMappingNoLongerInTheList_DoNothing()
    {
        var gone = Mapping("Gone", EventActionTriggerKind.Reward, new StubAction());
        var editor = new EventActionMappingsEditor([Mapping("Hydrate")]);

        Assert.Null(editor.AppendAction(gone, new StubAction()));
        Assert.Null(editor.ReplaceActions(gone, []));
        Assert.Equal("Hydrate", Assert.Single(editor.Mappings).RewardTitle);
    }

    [Fact]
    public void RemoveActions_RemovesMatchesFromEveryMapping_AndCountsThem()
    {
        var keep = new StubAction(kind: "keep");
        var untouched = Mapping("Untouched", EventActionTriggerKind.Reward, keep);
        var editor = new EventActionMappingsEditor(
        [
            Mapping("One", EventActionTriggerKind.Reward, new StubAction(kind: "drop"), keep),
            untouched,
            Mapping("Two", EventActionTriggerKind.Reward, new StubAction(kind: "drop"), new StubAction(kind: "drop")),
        ]);

        var removed = editor.RemoveActions(a => a.Kind == "drop");

        Assert.Equal(3, removed);
        Assert.Equal([keep], editor.Mappings[0].Actions);
        Assert.Same(untouched, editor.Mappings[1]);
        Assert.Empty(editor.Mappings[2].Actions);
    }

    [Fact]
    public void RemoveActions_WithNoMatch_ReturnsZeroAndChangesNothing()
    {
        var mapping = Mapping("One", EventActionTriggerKind.Reward, new StubAction());
        var editor = new EventActionMappingsEditor([mapping]);

        Assert.Equal(0, editor.RemoveActions(_ => false));
        Assert.Same(mapping, editor.Mappings[0]);
    }
}
