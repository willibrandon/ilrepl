using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// The status bar keeps the hints that fit beside the other sections and drops the rest from
/// the left, and it shows copy mode's keys while copy mode is on.
/// </summary>
[TestClass]
public sealed class StatusHintsTests
{
    private static readonly string[] s_facts = ["stack [int32, int32]", "no locals", "2 instructions"];
    private static readonly string[] s_methodFacts = ["method Fib", "stack [int32, int32]", "no locals", "2 instructions"];
    private static readonly string[] s_classFacts = ["class Point", "method Sum", "stack [int32, int32]", "no locals", "2 instructions"];
    private static readonly string[] s_all = ["Tab complete", "Shift+↑ select", "Ctrl+Q quit"];
    private static readonly string[] s_two = ["Shift+↑ select", "Ctrl+Q quit"];
    private static readonly string[] s_one = ["Ctrl+Q quit"];
    private static readonly string[] s_copyAll = ["Shift+↑↓ extend", "y yank", "Esc cancel"];
    private static readonly string[] s_copyTwo = ["y yank", "Esc cancel"];
    private static readonly string[] s_copyOne = ["Esc cancel"];

    /// <summary>
    /// Every hint fits in a wide terminal; a narrow one keeps the last hint alone.
    /// </summary>
    [TestMethod]
    public void Hints_DropFromTheLeftAsWidthShrinks()
    {
        Assert.AreSequenceEqual(s_all, IlReplApp.StatusHints(s_facts, 100, copyMode: false));
        Assert.AreSequenceEqual(s_two, IlReplApp.StatusHints(s_facts, 83, copyMode: false));
        Assert.AreSequenceEqual(s_one, IlReplApp.StatusHints(s_facts, 60, copyMode: false));
        Assert.AreSequenceEqual(s_one, IlReplApp.StatusHints(s_facts, 20, copyMode: false));
    }

    /// <summary>
    /// Copy mode shows its own keys, dropped the same way.
    /// </summary>
    [TestMethod]
    public void CopyMode_ShowsItsKeys()
    {
        Assert.AreSequenceEqual(s_copyAll, IlReplApp.StatusHints(s_facts, 100, copyMode: true));
        Assert.AreSequenceEqual(s_copyTwo, IlReplApp.StatusHints(s_facts, 83, copyMode: true));
        Assert.AreSequenceEqual(s_copyOne, IlReplApp.StatusHints(s_facts, 20, copyMode: true));
    }

    /// <summary>
    /// Before the size is known nothing is dropped.
    /// </summary>
    [TestMethod]
    public void UnknownWidth_KeepsEveryHint()
    {
        Assert.HasCount(3, IlReplApp.StatusHints(s_facts, 0, copyMode: false));
    }

    /// <summary>
    /// The method fact takes its width from the hints, which drop from the left as before.
    /// </summary>
    [TestMethod]
    public void MethodFact_TakesRoomFromTheHints()
    {
        Assert.AreSequenceEqual(s_all, IlReplApp.StatusHints(s_methodFacts, 110, copyMode: false));
        Assert.AreSequenceEqual(s_two, IlReplApp.StatusHints(s_methodFacts, 100, copyMode: false));
        Assert.AreSequenceEqual(s_one, IlReplApp.StatusHints(s_methodFacts, 83, copyMode: false));
    }

    /// <summary>
    /// A class fact before the method fact takes room the same way.
    /// </summary>
    [TestMethod]
    public void ClassFact_TakesRoomFromTheHints()
    {
        Assert.AreSequenceEqual(s_all, IlReplApp.StatusHints(s_classFacts, 125, copyMode: false));
        Assert.AreSequenceEqual(s_two, IlReplApp.StatusHints(s_classFacts, 110, copyMode: false));
        Assert.AreSequenceEqual(s_one, IlReplApp.StatusHints(s_classFacts, 90, copyMode: false));
    }

    private static readonly string[] s_continue = ["Ctrl+C clears", "Ctrl+Q quit", "Enter continues"];
    private static readonly string[] s_accept = ["Esc dismiss", "Ctrl+Q quit", "Enter accepts"];
    private static readonly string[] s_busy = ["Ctrl+Q quit", "Ctrl+C cancels"];
    private const string Block = ".method void F() {\n  nop\n  ret\n}";

    /// <summary>
    /// A pasted block is many lines, yet its braces balance, so Enter sends it and the bar says so.
    /// </summary>
    [TestMethod]
    public void StatusHints_CompletePastedBlock_SaysSends()
    {
        var state = NewState(Block);
        Assert.AreEqual(EnterAction.Submit, PromptWidget.EnterActionFor(state, paletteVisible: false, openDepth: 0, commentOpen: false));
        Assert.AreSequenceEqual(["Ctrl+C clears", "Ctrl+Q quit", "Enter sends 4 lines"], IlReplApp.StatusHints(s_facts, 100, copyMode: false, EnterAction.Submit, state.LineCount));
    }

    /// <summary>
    /// The final brace flips the hint from continue to send in the same keystroke.
    /// </summary>
    [TestMethod]
    public void StatusHints_JustTypedFinalBrace_SaysSends()
    {
        var open = NewState(Block[..^1]);
        var closed = NewState(Block);
        var before = PromptWidget.EnterActionFor(open, paletteVisible: false, openDepth: 0, commentOpen: false);
        var after = PromptWidget.EnterActionFor(closed, paletteVisible: false, openDepth: 0, commentOpen: false);
        Assert.AreEqual(EnterAction.Continue, before);
        Assert.AreEqual(EnterAction.Submit, after);
        Assert.Contains("Enter continues", IlReplApp.StatusHints(s_facts, 100, copyMode: false, before, open.LineCount));
        Assert.Contains("Enter sends 4 lines", IlReplApp.StatusHints(s_facts, 100, copyMode: false, after, closed.LineCount));
    }

    /// <summary>
    /// An open block continues, and that hint is the one a narrow bar keeps.
    /// </summary>
    [TestMethod]
    public void StatusHints_OpenBlock_SaysContinues()
    {
        var state = NewState(".method void F() {");
        Assert.AreEqual(EnterAction.Continue, PromptWidget.EnterActionFor(state, paletteVisible: false, openDepth: 0, commentOpen: false));
        Assert.AreSequenceEqual(s_continue, IlReplApp.StatusHints(s_facts, 100, copyMode: false, EnterAction.Continue, 1));
        Assert.AreSequenceEqual(["Ctrl+Q quit", "Enter continues"], IlReplApp.StatusHints(s_facts, 90, copyMode: false, EnterAction.Continue, 1));
        Assert.AreSequenceEqual(["Enter continues"], IlReplApp.StatusHints(s_facts, 70, copyMode: false, EnterAction.Continue, 1));
    }

    /// <summary>
    /// An unterminated block comment keeps the buffer open just as a brace does.
    /// </summary>
    [TestMethod]
    public void StatusHints_OpenComment_SaysContinues()
    {
        var state = NewState("/* a note");
        Assert.AreEqual(EnterAction.Continue, PromptWidget.EnterActionFor(state, paletteVisible: false, openDepth: 0, commentOpen: false));
        Assert.AreSequenceEqual(s_continue, IlReplApp.StatusHints(s_facts, 100, copyMode: false, EnterAction.Continue, 1));
    }

    /// <summary>
    /// A palette selection the user moved to is what Enter accepts; a palette nobody navigated is not.
    /// </summary>
    [TestMethod]
    public void StatusHints_PaletteNavigated_SaysAccepts()
    {
        var state = NewState("ldc.i4.");
        Assert.AreEqual(EnterAction.Submit, PromptWidget.EnterActionFor(state, paletteVisible: true, openDepth: 0, commentOpen: false));
        state.PaletteNavigated = true;
        Assert.AreEqual(EnterAction.AcceptCompletion, PromptWidget.EnterActionFor(state, paletteVisible: true, openDepth: 0, commentOpen: false));
        Assert.AreSequenceEqual(s_accept, IlReplApp.StatusHints(s_facts, 100, copyMode: false, EnterAction.AcceptCompletion, 1));
    }

    /// <summary>
    /// While a submission is in flight the bar offers cancel, and keeps that offer on a narrow bar;
    /// Enter itself still decides by the buffer, and what it sends waits its turn.
    /// </summary>
    [TestMethod]
    public async Task StatusHints_Busy_ShowsProgress()
    {
        await using var engine = new InProcessEngine();
        var state = NewState("");
        state.Submission = new Submission(engine, [], 0, false, _ => Task.CompletedTask, _ => { });
        Assert.IsTrue(state.Busy);
        Assert.AreEqual(EnterAction.Submit, PromptWidget.EnterActionFor(state, paletteVisible: false, openDepth: 0, commentOpen: false));
        Assert.AreSequenceEqual(s_busy, IlReplApp.StatusHints(s_facts, 100, copyMode: false, EnterAction.Busy, 1));
        Assert.AreSequenceEqual(["Ctrl+C cancels"], IlReplApp.StatusHints(s_facts, 40, copyMode: false, EnterAction.Busy, 1));
    }

    /// <summary>
    /// The hint and the handler read the same decision: whatever Enter will do, the bar names it.
    /// </summary>
    [TestMethod]
    public void EnterAction_MatchesHandler()
    {
        (string Text, EnterAction Action, string Hint)[] rows =
        [
            ("nop", EnterAction.Submit, "Tab complete"),
            (".method void F() {", EnterAction.Continue, "Enter continues"),
            ("/* open", EnterAction.Continue, "Enter continues"),
            (".method void F() {\n  /* open */ nop\n}", EnterAction.Submit, "Enter sends 3 lines"),
            (Block, EnterAction.Submit, "Enter sends 4 lines"),
        ];
        foreach (var (text, action, hint) in rows)
        {
            var state = NewState(text);
            var actual = PromptWidget.EnterActionFor(state, paletteVisible: false, openDepth: 0, commentOpen: false);
            Assert.AreEqual(action, actual, text);
            Assert.Contains(hint, IlReplApp.StatusHints(s_facts, 120, copyMode: false, actual, state.LineCount), text);
        }

        // A block the engine already has open continues until the buffer closes it.
        var closing = NewState("  ret\n}");
        Assert.AreEqual(EnterAction.Submit, PromptWidget.EnterActionFor(closing, paletteVisible: false, openDepth: 1, commentOpen: false));
        Assert.AreEqual(EnterAction.Continue, PromptWidget.EnterActionFor(NewState("  ret"), paletteVisible: false, openDepth: 1, commentOpen: false));
        Assert.AreEqual(EnterAction.Continue, PromptWidget.EnterActionFor(NewState("still open"), paletteVisible: false, openDepth: 0, commentOpen: true));
        Assert.AreEqual(EnterAction.Submit, PromptWidget.EnterActionFor(NewState("closed */"), paletteVisible: false, openDepth: 0, commentOpen: true));
    }

    private static PromptState NewState(string text)
    {
        var state = new PromptState(new PromptHistory(), new CilTokenizer(CilVocabularyBuilder.Vocabulary));
        state.SetText(text, text.Length);
        return state;
    }
}

