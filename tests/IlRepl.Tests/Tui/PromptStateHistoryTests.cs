using Hex1b.Documents;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Checks navigation entered before stored history arrives without losing later editor changes.
/// </summary>
[TestClass]
public sealed class PromptStateHistoryTests
{
    /// <summary>
    /// Early Up presses recall the requested entry and Down returns to the original draft.
    /// </summary>
    /// <param name="count">The number of early Up presses.</param>
    /// <param name="expected">The entry recalled when loading completes.</param>
    [TestMethod]
    [DataRow(1, "ldc.i4.2")]
    [DataRow(2, "ldc.i4.1")]
    public async Task LoadHistory_DeferredNavigation_PreservesTheDraft(int count, string expected)
    {
        await using var engine = new InProcessEngine();
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary)) { HistoryLoading = true };
        state.SetText("draft", 5);
        for (var index = 0; index < count; index++)
        {
            state.NavigateHistory(back: true);
        }

        Assert.AreEqual("draft", state.Text);
        state.LoadHistory(new HistorySnapshot(["ldc.i4.1", "ldc.i4.2"], 0));
        Assert.AreEqual(expected, state.Text);
        Assert.IsTrue(state.PaletteDismissed);
        for (var index = 0; index < count; index++)
        {
            state.NavigateHistory(back: false);
        }

        Assert.AreEqual("draft", state.Text);
    }

    /// <summary>
    /// Editing, clearing, moving the caret or pressing Down cancels pending navigation.
    /// </summary>
    /// <param name="action">The action taken after the early Up press.</param>
    [TestMethod]
    [DataRow("edit")]
    [DataRow("clear")]
    [DataRow("move")]
    [DataRow("down")]
    public async Task LoadHistory_ChangedIntent_LeavesTheEditorAlone(string action)
    {
        await using var engine = new InProcessEngine();
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary)) { HistoryLoading = true };
        state.SetText("draft", 5);
        state.NavigateHistory(back: true);
        switch (action)
        {
            case "edit":
                state.Editor.Document.Apply(new InsertOperation(new DocumentOffset(5), " changed"));
                break;
            case "clear":
                state.Clear();
                break;
            case "move":
                state.Editor.SetCursorPosition(DocumentOffset.Zero);
                break;
            default:
                state.NavigateHistory(back: false);
                break;
        }

        var expected = state.Text;
        var caret = state.Editor.Cursor.Position;
        state.LoadHistory(new HistorySnapshot(["ldc.i4.1"], 0));
        Assert.AreEqual(expected, state.Text);
        Assert.AreEqual(caret, state.Editor.Cursor.Position);
        Assert.HasCount(1, state.History.Entries);
        Assert.IsFalse(state.HistoryLoading);
    }

    /// <summary>
    /// Browsing past a newly submitted entry continues into stored history after loading.
    /// </summary>
    [TestMethod]
    public async Task LoadHistory_DeferredPastNewEntries_ContinuesAtTheRightEntry()
    {
        await using var engine = new InProcessEngine();
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary)) { HistoryLoading = true };
        state.History.Add("nop");
        state.NavigateHistory(back: true);
        Assert.AreEqual("nop", state.Text);
        state.NavigateHistory(back: true);
        state.LoadHistory(new HistorySnapshot(["ldc.i4.1"], 0));
        Assert.AreEqual("ldc.i4.1", state.Text);
        state.NavigateHistory(back: false);
        Assert.AreEqual("nop", state.Text);
        state.NavigateHistory(back: false);
        Assert.AreEqual("", state.Text);
    }
}
