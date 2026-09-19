using Hex1b;
using Hex1b.Documents;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Checks contextual help precedence, source actions, wrapping, and editor preservation with real engine analysis.
/// </summary>
[TestClass]
public sealed class PromptHelpTests
{
    /// <summary>
    /// Supplies cancellation for analysis and engine operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A real assembly load during help selection preserves coherent text and retires diagnostic navigation until analysis refreshes.
    /// </summary>
    /// <param name="source">The actual incomplete or invalid IL analyzed by the engine.</param>
    /// <param name="mnemonic">The instruction whose documentation remains available.</param>
    /// <param name="hasProducer">Whether the diagnostic identifies an earlier instruction in the editor.</param>
    [TestMethod]
    [DataRow("constr", "constrained.", false)]
    [DataRow("ldc.i4.1\nthrow", "throw", true)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AssemblyLoad_DuringHelpSelectionPreservesEvidenceAndFreshness(string source, string mnemonic, bool hasProducer)
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        var token = TestContext.CancellationToken;
        await using var engine = new CompletionEngine { HoldCompletion = false };
        await engine.PrimeAsync(token);
        using var invalidated = new SemaphoreSlim(0);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary))
        {
            Analyzer = new AnalysisRequester(engine), Invalidate = () => invalidated.Release(),
            CurrentHelpIdentity = () => (engine.Status.Revision, engine.AssemblyVersion),
        };

        state.SetText(source, source.Length);
        try
        {
            await FreshAnalysisAsync();
            var before = engine.AssemblyVersion;
            state.HelpRevision = engine.Status.Revision;
            state.HelpAssemblyVersion = before;
            var display = PromptDiagnostics.Display(state);
            Assert.IsNotNull(display);
            var analysis = state.Analysis;
            Assert.IsNotNull(analysis);
            var version = state.Editor.Document.Version;
            var caret = state.Editor.Cursor.Position;
            var undo = state.Editor.History.UndoCount;
            var loaded = false;
            var catalog = new ObservedCompletionCatalog(engine.Catalog, () =>
            {
                CompletionEngine.ChangeAssemblies();
                loaded = true;
                Assert.IsGreaterThan(before, engine.AssemblyVersion);
            });

            PromptHelp.Open(state, catalog);

            Assert.IsTrue(loaded, "The real assembly load must occur while help is choosing its completion.");
            Assert.IsNotNull(state.Analysis);
            Assert.AreSame(analysis.Diagnostics, state.Analysis.Diagnostics);
            Assert.AreEqual(before, state.Analysis.AssemblyVersion, "The newer catalog must not fabricate replacement evidence.");
            var help = state.Help;
            Assert.IsNotNull(help);
            Assert.AreEqual("help · " + mnemonic, help.Heading);
            Assert.AreEqual(display.Text, help.Lines(200)[0].Text);
            Assert.IsFalse(help.IsCurrent(state));
            var sources = help.Actions.Select((action, index) => (action, index)).Where(item => item.action.Source is not null).ToArray();
            Assert.AreEqual(hasProducer, sources.Length != 0);
            foreach (var (_, index) in sources)
            {
                Assert.IsFalse(help.IsActionCurrent(state, index));
            }

            if (hasProducer)
            {
                help.Activate(state);
                Assert.AreSame(help, state.Help);
                Assert.AreEqual(caret, state.Editor.Cursor.Position);
            }

            string? opened = null;
            state.OpenDocumentation = url => opened = url;
            help.MoveAction(state, backwards: true, 80, 8);
            help.Activate(state);
            Assert.AreEqual(InstructionReference.For(mnemonic).DocumentationUrl, opened);
            Assert.AreEqual(source, state.Text);
            Assert.AreEqual(version, state.Editor.Document.Version);
            Assert.AreEqual(caret, state.Editor.Cursor.Position);
            Assert.AreEqual(undo, state.Editor.History.UndoCount);

            await FreshAnalysisAsync();
            state.HelpAssemblyVersion = engine.AssemblyVersion;
            help.Refresh(state, engine.Catalog);
            Assert.IsTrue(help.IsCurrent(state));
            if (hasProducer)
            {
                help.MoveAction(state, backwards: false, 80, 8);
                Assert.IsNotNull(help.Actions[help.SelectedAction].Source);
                Assert.IsTrue(help.IsActionCurrent(state, help.SelectedAction));
                help.Activate(state);
                Assert.IsNull(state.Help);
                Assert.AreEqual(1, state.CaretLine);
                Assert.AreEqual(source, state.Text);
            }
        }
        finally
        {
            await state.Analyzer.SettleAsync(TimeSpan.FromSeconds(5));
        }

        async Task FreshAnalysisAsync()
        {
            while (true)
            {
                state.Analyzer.Refresh(state);
                if (state.Analysis is { } current && current.AssemblyVersion == engine.AssemblyVersion)
                {
                    return;
                }

                Assert.IsTrue(await invalidated.WaitAsync(TimeSpan.FromSeconds(5), token));
            }
        }
    }

    /// <summary>
    /// A caret diagnostic is presented first and exposes its required type, actual stack, and producer.
    /// </summary>
    [TestMethod]
    public async Task Open_PrefersCaretDiagnosticAndRetainsCompleteEvidence()
    {
        await using var engine = new InProcessEngine();
        var state = await StateAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        PromptHelp.Open(state, engine.Catalog);
        Assert.IsNotNull(state.Help);
        var lines = state.Help.Lines(120).Select(line => line.Text).ToArray();
        Assert.StartsWith("error on line 2:", lines[0]);
        Assert.Contains("argument 1", lines[0]);
        Assert.Contains("Stack before (bottom → top): [string]", lines);
        Assert.Contains(line => line.Contains("expected int32; actual string", StringComparison.Ordinal), lines);
        var source = Assert.ContainsSingle(state.Help.Actions.Where(action => action.Source is not null)).Source!;
        Assert.AreEqual(0, source.Location.Line);
        Assert.AreEqual("ldstr \"wrong\"", source.Source);
        Assert.AreEqual(AnalysisSourceKind.Document, source.Kind);
    }

    /// <summary>
    /// A selected completion takes precedence over fallback caret help while a dismissed palette uses the caret instruction.
    /// </summary>
    [TestMethod]
    public async Task Open_PrefersSelectedCompletionThenCaretHelp()
    {
        await using var engine = new InProcessEngine();
        var state = await StateAsync(engine, "ad");
        state.Analysis = state.Analysis! with { Diagnostics = [], InstructionHelp = InstructionReference.For("nop") };
        var candidates = PromptWidget.Candidates(state, engine.Catalog);
        state.SelectedIndex = candidates.ToList().FindIndex(item => item.Name == "add.ovf");
        Assert.IsGreaterThanOrEqualTo(0, state.SelectedIndex);
        PromptHelp.Open(state, engine.Catalog);
        Assert.IsNotNull(state.Help);
        Assert.AreEqual("add.ovf", state.Help.Lines(120)[0].Text);
        Assert.AreEqual(InstructionReference.For("add.ovf").DocumentationUrl, Assert.ContainsSingle(state.Help.Actions).Url);
        PromptHelp.Close(state);
        state.PaletteDismissed = true;
        PromptHelp.Open(state, engine.Catalog);
        Assert.AreEqual("nop", state.Help!.Lines(120)[0].Text);
        Assert.AreEqual(InstructionReference.For("nop").DocumentationUrl, Assert.ContainsSingle(state.Help.Actions).Url);
    }

    /// <summary>
    /// Opening, scrolling, selecting a link, and closing help preserve the document, selection, undo history, and completion index.
    /// </summary>
    [TestMethod]
    public async Task OpenAndClose_PreserveSelectionUndoAndCompletionSelection()
    {
        await using var engine = new InProcessEngine();
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        state.SetText("ldc.i4.", 7);
        state.Editor.InsertText("1");
        state.SelectLine(0);
        state.SelectedIndex = 2;
        state.Analysis = await AnalyzeAsync(engine, state);
        var version = state.Editor.Document.Version;
        var selection = state.Editor.Cursor.SelectionRange;
        var caret = state.Editor.Cursor.Position;

        PromptHelp.Open(state, engine.Catalog);
        Assert.IsNotNull(state.Help);
        state.Help.Scroll = 10;
        state.Help.MoveAction(state, backwards: false, 12, 3);
        Assert.IsNotEmpty(state.Help.Lines(12));
        PromptHelp.Close(state);

        Assert.IsNull(state.Help);
        Assert.AreEqual("ldc.i4.1", state.Text);
        Assert.AreEqual(version, state.Editor.Document.Version);
        Assert.AreEqual(selection, state.Editor.Cursor.SelectionRange);
        Assert.AreEqual(caret, state.Editor.Cursor.Position);
        Assert.AreEqual(2, state.SelectedIndex);
        state.Editor.Undo();
        Assert.AreEqual("ldc.i4.", state.Text);
        state.Editor.Redo();
        Assert.AreEqual("ldc.i4.1", state.Text);
    }

    /// <summary>
    /// A fresh source action moves to its producer without changing text, while documentation activation reaches the host callback.
    /// </summary>
    [TestMethod]
    public async Task Activate_UsesFreshSourceAndDocumentationTargets()
    {
        await using var engine = new InProcessEngine();
        const string text = "  ldstr \"wrong\"\ncall int32 Math::Abs(int32)";
        var state = await StateAsync(engine, text);
        string? opened = null;
        state.OpenDocumentation = url => opened = url;
        PromptHelp.Open(state, engine.Catalog);
        var help = state.Help!;
        Assert.AreEqual(0, help.SelectedAction);
        Assert.IsNotNull(help.Actions[0].Source);
        help.MoveAction(state, backwards: true, 60, 5);
        Assert.AreEqual(help.Actions.Count - 1, help.SelectedAction);
        help.Activate(state);
        Assert.AreEqual(InstructionReference.For("call").DocumentationUrl, opened);
        Assert.AreEqual(text.Length, state.Editor.Cursor.Position.Value);
        help.MoveAction(state, backwards: false, 60, 5);
        Assert.AreEqual(0, help.SelectedAction);
        help.Activate(state);
        Assert.IsNull(state.Help);
        Assert.AreEqual(text, state.Text);
        Assert.AreEqual(new DocumentPosition(1, 3), state.Editor.Document.OffsetToPosition(state.Editor.Cursor.Position));
    }

    /// <summary>
    /// Closing or replacing help retires its actions even when the source, caret, and analysis are unchanged.
    /// </summary>
    /// <param name="replace">Whether another help instance has replaced the retained one.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClosedHelp_CannotActivateRetainedActions(bool replace)
    {
        await using var engine = new InProcessEngine();
        var state = await StateAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        PromptHelp.Open(state, engine.Catalog);
        var retired = state.Help!;
        var caret = state.Editor.Cursor.Position;
        PromptHelp.Close(state);
        if (replace)
        {
            PromptHelp.Open(state, engine.Catalog);
        }

        var current = state.Help;

        Assert.IsFalse(retired.IsCurrent(state));
        retired.Activate(state);

        Assert.AreEqual(caret, state.Editor.Cursor.Position);
        Assert.AreSame(current, state.Help);
        Assert.AreEqual("ldstr \"wrong\"\ncall int32 Math::Abs(int32)", state.Text);
    }

    /// <summary>
    /// Retained help cannot navigate or open documentation after the document changes and new analysis is pending.
    /// </summary>
    [TestMethod]
    public async Task StaleHelp_RetainsTextButCannotActivateActions()
    {
        await using var engine = new InProcessEngine();
        var state = await StateAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        string? opened = null;
        state.OpenDocumentation = url => opened = url;
        PromptHelp.Open(state, engine.Catalog);
        var help = state.Help!;
        help.MoveAction(state, backwards: true, 80, 8);
        var rows = help.Lines(80).Select(line => line.Text).ToArray();
        state.Editor.InsertText(" // changed");
        var position = state.Editor.Cursor.Position;
        state.Analyzer = new AnalysisRequester(engine);
        state.Analyzer.Refresh(state);
        Assert.IsFalse(help.IsCurrent(state));
        Assert.AreSequenceEqual(rows, help.Lines(80).Select(line => line.Text));
        help.Activate(state);
        help.MoveAction(state, backwards: false, 80, 8);
        help.Activate(state);
        Assert.IsNull(opened);
        Assert.AreEqual(position, state.Editor.Cursor.Position);
        Assert.AreEqual(help.Actions.Count - 1, help.SelectedAction);
        await state.Analyzer.SettleAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// An assembly load preserves a catalogue link, but editing its source retires the retained action.
    /// </summary>
    [TestMethod]
    public async Task CatalogueLink_RemainsBoundToDocumentAfterAssemblyLoad()
    {
        await using var engine = new InProcessEngine();
        var state = await StateAsync(engine, "constr");
        string? opened = null;
        state.OpenDocumentation = url => opened = url;
        PromptHelp.Open(state, engine.Catalog);
        var help = state.Help!;
        Assert.AreEqual("incomplete on line 1: finish opcode 'constr'; Tab completes it", help.Lines(120)[0].Text);
        Assert.IsTrue(help.IsCurrent(state));

        Assert.IsTrue((await engine.HandleAsync(".load " + SampleHost.Samples.GreeterDll,
            TestContext.CancellationToken)).Succeeded);
        state.Analysis = await AnalyzeAsync(engine, state);
        Assert.IsFalse(help.IsCurrent(state), "The retained diagnostic must be refreshed from the new analysis.");
        help.Activate(state);
        Assert.AreEqual(InstructionReference.For("constrained.").DocumentationUrl, opened);

        opened = null;
        state.Editor.InsertText("a");
        help.Activate(state);
        Assert.IsNull(opened, "The retained catalogue link must not outlive its source document.");
        Assert.AreEqual("constra", state.Text);
    }

    /// <summary>
    /// Refreshing analysis keeps an unchanged instruction's documentation navigable while source actions await fresh evidence.
    /// </summary>
    [TestMethod]
    public async Task DiagnosticDocumentationLink_RemainsNavigableDuringAnalysisRefresh()
    {
        await using var engine = new InProcessEngine();
        var state = await StateAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        string? opened = null;
        state.OpenDocumentation = url => opened = url;
        PromptHelp.Open(state, engine.Catalog);
        var help = state.Help!;
        Assert.IsNotNull(help.Actions[0].Source);

        state.Analysis = await AnalyzeAsync(engine, state);

        Assert.IsFalse(help.IsCurrent(state));
        Assert.IsFalse(help.IsActionCurrent(state, 0));
        help.MoveAction(state, backwards: false, 80, 8);
        Assert.AreEqual(InstructionReference.For("call").DocumentationUrl, help.Actions[help.SelectedAction].Url);
        help.Activate(state);
        Assert.AreEqual(InstructionReference.For("call").DocumentationUrl, opened);
        help.Refresh(state, engine.Catalog);
        Assert.AreEqual(InstructionReference.For("call").DocumentationUrl, help.Actions[help.SelectedAction].Url);
    }

    /// <summary>
    /// New analysis after an assembly load retires diagnostic source navigation until help refreshes its evidence.
    /// </summary>
    [TestMethod]
    public async Task AssemblyLoad_RetiresDiagnosticSourceAction()
    {
        await using var engine = new InProcessEngine();
        var state = await StateAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        PromptHelp.Open(state, engine.Catalog);
        var help = state.Help!;
        Assert.IsNotNull(help.Actions[help.SelectedAction].Source);
        var caret = state.Editor.Cursor.Position;

        Assert.IsTrue((await engine.HandleAsync(".load " + SampleHost.Samples.GreeterDll,
            TestContext.CancellationToken)).Succeeded);
        state.Analysis = await AnalyzeAsync(engine, state);
        help.Activate(state);

        Assert.AreSame(help, state.Help);
        Assert.AreEqual(caret, state.Editor.Cursor.Position);
        Assert.AreEqual("ldstr \"wrong\"\ncall int32 Math::Abs(int32)", state.Text);
    }

    /// <summary>
    /// F1 and diagnostic navigation retire previous analysis when input arrives before the next root render.
    /// </summary>
    /// <param name="action">The contextual action invoked immediately after editing.</param>
    [TestMethod]
    [DataRow("help")]
    [DataRow("next")]
    [DataRow("previous")]
    public async Task InputBeforeRefresh_CannotActivatePreviousDocumentEvidence(string action)
    {
        await using var engine = new InProcessEngine();
        var state = await StateAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        using var invalidated = new SemaphoreSlim(0);
        state.Invalidate = () => invalidated.Release();
        state.Analyzer = new AnalysisRequester(engine);
        try
        {
            state.Analyzer.Refresh(state);
            while (state.Analysis is null)
            {
                Assert.IsTrue(await invalidated.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken));
                state.Analyzer.Refresh(state);
            }

            var previous = state.Analysis;
            var diagnostic = Assert.ContainsSingle(previous.Diagnostics.Where(item => item.Kind == AnalysisDiagnosticKind.Error));
            Assert.IsNotNull(diagnostic.Explanation);
            Assert.IsNotEmpty(diagnostic.Explanation.Conflicts.SelectMany(conflict => conflict.Producers));
            state.SelectLine(0);
            state.Editor.InsertText("ldc.i4.1");
            state.Editor.SetCursorPosition(new DocumentOffset(state.Editor.Document.Length));
            var position = state.Editor.Cursor.Position;
            Assert.AreSame(previous, state.Analysis, "The input has arrived before the root refreshes its analysis.");

            if (action == "help")
            {
                PromptHelp.Open(state, engine.Catalog);
                var help = state.Help;
                Assert.IsNotNull(help);
                Assert.DoesNotContain(item => item.Source is not null, help.Actions);
                help.Activate(state);
            }
            else
            {
                PromptDiagnostics.Move(state, backwards: action == "previous");
            }

            Assert.AreNotSame(previous, state.Analysis);
            Assert.AreEqual(position, state.Editor.Cursor.Position);
            Assert.AreEqual("ldc.i4.1\ncall int32 Math::Abs(int32)", state.Text);
        }
        finally
        {
            await state.Analyzer.SettleAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Previously accepted values remain useful evidence without becoming navigable locations in a new editor document.
    /// </summary>
    [TestMethod]
    public async Task AcceptedProducer_IsDisplayedWithoutAnEditorAction()
    {
        await using var engine = new InProcessEngine();
        const string source = "ldstr \"previous\"";
        Assert.IsTrue((await engine.HandleSourceAsync(source, new("previous", 12, 0, source.Length),
            TestContext.CancellationToken)).Succeeded);
        var state = await StateAsync(engine, "call int32 Math::Abs(int32)");
        PromptHelp.Open(state, engine.Catalog);
        Assert.IsNotNull(state.Help);
        Assert.DoesNotContain(action => action.Source is not null, state.Help.Actions);
        Assert.Contains(line => line.Text.Contains(source, StringComparison.Ordinal), state.Help.Lines(120));
        Assert.AreEqual(InstructionReference.For("call").DocumentationUrl, Assert.ContainsSingle(state.Help.Actions).Url);
    }

    /// <summary>
    /// A primary diagnostic outside the current document neither matches its caret nor redirects diagnostic navigation.
    /// </summary>
    /// <param name="kind">The non-document source of the rejected instruction.</param>
    [TestMethod]
    [DataRow(AnalysisSourceKind.Accepted)]
    [DataRow(AnalysisSourceKind.Imported)]
    [DataRow(AnalysisSourceKind.Synthetic)]
    public async Task ExternalPrimaryDiagnostic_CannotNavigateCurrentDocument(AnalysisSourceKind kind)
    {
        await using var engine = new InProcessEngine();
        var state = await StateAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        var diagnostic = Assert.ContainsSingle(state.Analysis!.Diagnostics.Where(item => item.Kind == AnalysisDiagnosticKind.Error));
        Assert.IsNotNull(diagnostic.Explanation);
        var location = diagnostic.Location with { Body = "previous", Offset = kind == AnalysisSourceKind.Imported ? 17 : null };
        diagnostic = diagnostic with
        {
            Location = location,
            Explanation = diagnostic.Explanation with { Source = new(location, "call int32 Math::Abs(int32)", kind) },
        };

        state.Analysis = state.Analysis with { Diagnostics = [diagnostic] };
        var caret = state.Editor.Cursor.Position;
        PromptDiagnostics.Move(state, backwards: false);
        Assert.AreEqual(caret, state.Editor.Cursor.Position);
        PromptDiagnostics.Move(state, backwards: true);
        Assert.AreEqual(caret, state.Editor.Cursor.Position);
        PromptHelp.Open(state, engine.Catalog);
        Assert.IsNotNull(state.Help);
        Assert.StartsWith("call ", state.Help.Lines(120)[0].Text);
        Assert.DoesNotContain(action => action.Source is not null, state.Help.Actions);
    }

    /// <summary>
    /// Narrow help wrapping preserves complete Unicode text elements and keeps every signature line and link reachable.
    /// </summary>
    [TestMethod]
    public async Task Lines_PreserveUnicodeAndCompleteContentAtNarrowWidths()
    {
        await using var engine = new InProcessEngine();
        const string producer = "ldstr \"界👩‍💻é\"";
        var state = await StateAsync(engine, producer + "\ncall int32 Math::Abs(int32)");
        PromptHelp.Open(state, engine.Catalog);
        Assert.IsNotNull(state.Help);
        var lines = state.Help.Lines(9);
        Assert.IsTrue(lines.All(line => DisplayWidth.GetStringWidth(line.Text) <= 9));
        var content = string.Concat(lines.Select(line => line.Text.Trim()));
        Assert.Contains("界👩‍💻é", content);
        Assert.Contains("Math::Abs(int32)", content);
        Assert.Contains(InstructionReference.For("call").DocumentationUrl, content);
        Assert.DoesNotContain(line => line.Text.Length > 0 && char.IsLowSurrogate(line.Text[0]), lines);
        Assert.DoesNotContain(line => line.Text.StartsWith('\u200d') || line.Text.StartsWith('\u0301'), lines);
    }

    /// <summary>
    /// Prose wrapping favors whole words while preserving whitespace, explicit empty lines, and indivisible text elements.
    /// </summary>
    [TestMethod]
    public void ProseWrapping_PreservesWordsSpacingAndGraphemes()
    {
        Assert.AreSequenceEqual(["push receiver", "before", "arguments"],
            PaletteText.WrapWords("push receiver before arguments", 14));
        Assert.AreSequenceEqual(["a ", "b"], PaletteText.WrapWords("a  b", 3));
        Assert.AreSequenceEqual(["ab", "cd"], PaletteText.WrapWords("ab cd", 2));
        Assert.AreSequenceEqual(["a bc", "de"], PaletteText.WrapWords("a bc de", 4));
        Assert.AreSequenceEqual(["  ", "ab", "cd"], PaletteText.WrapWords("  ab cd", 2));
        Assert.AreSequenceEqual(["  ", "  "], PaletteText.WrapWords("    ", 2));
        Assert.AreSequenceEqual(["a", "", "b", ""], PaletteText.WrapWords("a\r\n\r\nb\n", 8));
        Assert.AreSequenceEqual([""], PaletteText.WrapWords("", 2));
        Assert.AreSequenceEqual(["", ""], PaletteText.WrapWords("\r\n", 2));
        Assert.AreSequenceEqual(["a", "b", "c", "d"], PaletteText.WrapWords("ab cd", 0));
        Assert.AreSequenceEqual(["ab", "cd", "e"], PaletteText.WrapWords("abcde", 2));
        Assert.AreSequenceEqual(["界", "👩‍💻", "é"], PaletteText.WrapWords("界👩‍💻é", 2));
        Assert.AreSequenceEqual(["界", "👩‍💻", "é"], PaletteText.WrapWords("界👩‍💻é", 1));
    }

    private async Task<PromptState> StateAsync(InProcessEngine engine, string text)
    {
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        state.SetText(text, text.Length);
        state.Analysis = await AnalyzeAsync(engine, state);
        return state;
    }

    private Task<AnalysisReply> AnalyzeAsync(InProcessEngine engine, PromptState state) => engine.AnalyzeAsync(
        new(state.Text.Split('\n'), state.CaretLine - 1, state.CaretColumn, state.Editor.Document.Version), TestContext.CancellationToken);
}
