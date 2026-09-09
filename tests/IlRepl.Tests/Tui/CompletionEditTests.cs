using Hex1b;
using Hex1b.Documents;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Checks completion edits, generic owner anchors and Unicode layout independently of terminal timing.
/// </summary>
[TestClass]
public sealed class CompletionEditTests
{
    /// <summary>
    /// Replacing an operand preserves surrounding text and undo restores both the range and caret.
    /// </summary>
    [TestMethod]
    public async Task Apply_MidReference_PreservesSurroundingsAndUndo()
    {
        await using var engine = new InProcessEngine();
        const string original = "  call Console::WriteLine(string) // retained 😀";
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        state.SetText(original, 18);
        var edit = new CompletionEdit(new DocumentRange(new DocumentOffset(7), new DocumentOffset(33)), "Console::Write(string)");
        edit.Apply(state);
        Assert.AreEqual("  call Console::Write(string) // retained 😀", state.Text);
        Assert.AreEqual(29, state.CaretColumn);
        state.Editor.Undo();
        Assert.AreEqual(original, state.Text);
        Assert.AreEqual(18, state.CaretColumn);
        state.Editor.Redo();
        Assert.AreEqual("  call Console::Write(string) // retained 😀", state.Text);
    }

    /// <summary>
    /// Ghost text describes only an exact append to an unselected line end.
    /// </summary>
    /// <param name="typed">The operand typed so far.</param>
    /// <param name="expected">The visible suffix, or null for replacement edits.</param>
    [TestMethod]
    [DataRow("Console::Wr", "iteLine(string)")]
    [DataRow("console::wr", null)]
    [DataRow("Console::WL", null)]
    [DataRow("Console::WriteLine(string)", null)]
    public async Task Prediction_ExactAppendOnly_ShowsExpectedSuffix(string typed, string? expected)
    {
        await using var engine = new InProcessEngine();
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        state.SetText("call " + typed, typed.Length + 5);
        var edit = new CompletionEdit(new DocumentRange(new DocumentOffset(5), state.Editor.Cursor.Position),
            "Console::WriteLine(string)");
        Assert.AreEqual(expected, edit.Prediction(state));
        state.Editor.SetCursorPosition(new DocumentOffset(5), extend: true);
        Assert.IsNull(edit.Prediction(state));
    }

    /// <summary>
    /// Editing nested arguments retains each owner, while editing an owner's name removes only that selection.
    /// </summary>
    [TestMethod]
    public void Anchors_NestedArgumentEdits_RetainOwnersAndTranslateOffsets()
    {
        var document = new Hex1bDocument("call Host::Method<List<");
        using var anchors = new ContinuationAnchors(document);
        anchors.Add(5, 18, "outer");
        anchors.Add(18, 23, "inner");
        document.Apply(new InsertOperation(new DocumentOffset(23), "string"));
        Assert.HasCount(2, anchors.Snapshot());
        document.Apply(new InsertOperation(new DocumentOffset(0), "\n  "));
        var moved = anchors.Snapshot();
        Assert.AreEqual(1, moved[0].Line);
        Assert.AreEqual(7, moved[0].Start);
        document.Apply(new ReplaceOperation(new DocumentRange(new DocumentOffset(21), new DocumentOffset(25)), "HashSet"));
        var remaining = anchors.Snapshot();
        Assert.HasCount(1, remaining);
        Assert.AreEqual("outer", remaining[0].Token);
    }

    /// <summary>
    /// Clipping respects terminal cells and wrapping retains every Unicode text element.
    /// </summary>
    [TestMethod]
    public void PaletteText_WideAndCombiningCharacters_PreserveTextElements()
    {
        const string text = "界😀e\u0301界😀e\u0301";
        Assert.AreEqual("界…", PaletteText.Clip(text, 4));
        Assert.AreEqual("界😀", PaletteText.Clip(text, 4, ellipsis: false));
        var wrapped = PaletteText.Wrap(text, 5);
        Assert.AreEqual(text, string.Concat(wrapped));
        Assert.IsTrue(wrapped.All(line => DisplayWidth.GetStringWidth(line) <= 5));
        Assert.AreEqual(7, DisplayWidth.GetStringWidth(PaletteText.Column("界😀e\u0301", 7)));
    }

    /// <summary>
    /// A long signature receives scrollable detail space while preserving the prompt and candidate list.
    /// </summary>
    [TestMethod]
    public void Layout_LongDetail_LeavesListAndEditorVisible()
    {
        var fit = PromptLayout.Fit(24, 3, 100, 200);
        Assert.AreEqual(3, fit.EditorRows);
        Assert.IsGreaterThanOrEqualTo(3, fit.PaletteRows);
        Assert.IsGreaterThan(4, fit.DetailRows);
        Assert.AreEqual(24, fit.EditorRows + fit.PaletteRows + fit.DetailRows + fit.TranscriptRows + 4);
        Assert.Contains("Tab complete", IlReplApp.StatusHints([], 0, false, EnterAction.Continue, 3, paletteVisible: true));
    }
}
