using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Checks contextual help text and semantic span styles using real engine analysis and source provenance.
/// </summary>
[TestClass]
public sealed class PromptHelpContentTests
{
    /// <summary>
    /// Supplies cancellation for real analysis requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Diagnostic syntax appears once and preserves the instruction, member, type, and producer token styles.
    /// </summary>
    [TestMethod]
    public async Task Diagnostic_PresentsSyntaxOnceAndColorsSourceTokens()
    {
        await using var engine = new InProcessEngine();
        var state = await OpenAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        var rows = state.Help!.Rows(512);
        var diagnostic = PromptDiagnostics.Current(state)!;
        var instruction = diagnostic.Explanation!.Instruction!;
        var syntax = Assert.ContainsSingle(rows.Where(row => row.Line.PlainText == instruction.Syntax)).Line;

        Assert.AreEqual("help · call", state.Help.Heading);
        Assert.AreEqual("call", StyledText(syntax, SpanStyle.Opcode));
        Assert.AreEqual("Abs", StyledText(syntax, SpanStyle.Member));
        Assert.Contains("int32", StyledText(syntax, SpanStyle.Type));
        Assert.Contains("Math", StyledText(syntax, SpanStyle.Type));
        var expected = Assert.ContainsSingle(rows.Where(row => row.Line.PlainText.StartsWith("Expected: ", StringComparison.Ordinal)));
        Assert.AreSequenceEqual([new TranscriptSpan("Expected: ", SpanStyle.Dim), new("argument 1 must be int32")],
            expected.Line.Spans);
        var producer = Assert.ContainsSingle(rows.Where(row => row.Line.PlainText == "  from line 1: ldstr \"wrong\"")).Line;
        Assert.AreEqual("  from ", producer.Spans[0].Text);
        Assert.AreEqual(SpanStyle.Dim, producer.Spans[0].Style);
        Assert.AreEqual("ldstr", StyledText(producer, SpanStyle.Opcode));
        Assert.AreEqual("\"wrong\"", StyledText(producer, SpanStyle.String));
        AssertEvidenceMatchesFormatter(state);
    }

    /// <summary>
    /// Diagnostic evidence, instruction prose, and actions have distinct sections with secondary notes dimmed.
    /// </summary>
    [TestMethod]
    public async Task Diagnostic_SeparatesEvidenceInstructionAndActions()
    {
        await using var engine = new InProcessEngine();
        var state = await OpenAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        var rows = state.Help!.Rows(512);
        var instruction = PromptDiagnostics.Current(state)!.Explanation!.Instruction!;
        var effect = Enumerable.Range(0, rows.Count).Single(index => rows[index].Line.PlainText.StartsWith("Stack effect: ",
            StringComparison.Ordinal));
        var action = Enumerable.Range(0, rows.Count).First(index => rows[index].Action >= 0);

        Assert.AreEqual("", rows[effect - 1].Line.PlainText);
        Assert.AreEqual(-1, rows[effect - 1].Action);
        Assert.AreSequenceEqual([new TranscriptSpan(instruction.Explanation)], rows[effect + 1].Line.Spans);
        Assert.IsNotEmpty(instruction.Notes);
        foreach (var note in instruction.Notes)
        {
            var row = Assert.ContainsSingle(rows.Where(row => row.Line.PlainText == "• " + note));
            Assert.AreSequenceEqual([new TranscriptSpan("• " + note, SpanStyle.Dim)], row.Line.Spans);
        }

        Assert.AreEqual("", rows[action - 1].Line.PlainText);
        Assert.AreEqual(-1, rows[action - 1].Action);
        Assert.HasCount(2, rows.Where(row => row.Line.PlainText.Length == 0));
    }

    /// <summary>
    /// Help repeats the compact diagnostic's complete text and severity style for errors and incomplete instructions.
    /// </summary>
    /// <param name="source">The unsent source producing the diagnostic.</param>
    /// <param name="style">The expected severity style.</param>
    [TestMethod]
    [DataRow("ldstr \"wrong\"\ncall int32 Math::Abs(int32)", SpanStyle.Error)]
    [DataRow("constr", SpanStyle.Dim)]
    public async Task Preview_UsesDiagnosticDisplayTextAndSeverity(string source, SpanStyle style)
    {
        await using var engine = new InProcessEngine();
        var state = await OpenAsync(engine, source);
        var display = PromptDiagnostics.Display(state);
        Assert.IsNotNull(display);
        Assert.AreEqual(style, display.Style);

        var first = state.Help!.Rows(512)[0];

        Assert.AreEqual(-1, first.Action);
        Assert.AreEqual(display.Text, first.Line.PlainText);
        Assert.AreSequenceEqual([new TranscriptSpan(display.Text, style)], first.Line.Spans);
    }

    /// <summary>
    /// Cycling real source and documentation actions swaps their role styles without altering diagnostic evidence.
    /// </summary>
    [TestMethod]
    public async Task ActionSelection_UsesTopTypeAndMemberWithoutChangingEvidence()
    {
        await using var engine = new InProcessEngine();
        var state = await OpenAsync(engine, "ldstr \"wrong\"\ncall int32 Math::Abs(int32)");
        var help = state.Help!;
        var before = help.Rows(512);
        Assert.HasCount(2, help.Actions);
        Assert.IsNotNull(help.Actions[0].Source);
        Assert.IsNotNull(help.Actions[1].Url);
        AssertAction(before, 0, "❯ " + help.Actions[0].Label, SpanStyle.TopType);
        AssertAction(before, 1, "  " + help.Actions[1].Label, SpanStyle.Member);

        help.MoveAction(state, backwards: false, 512, 8);
        var after = help.Rows(512);

        Assert.AreEqual(1, help.SelectedAction);
        AssertAction(after, 0, "  " + help.Actions[0].Label, SpanStyle.Member);
        AssertAction(after, 1, "❯ " + help.Actions[1].Label, SpanStyle.TopType);
        Assert.AreSequenceEqual(before.Where(row => row.Action < 0).Select(row => row.Line.PlainText),
            after.Where(row => row.Action < 0).Select(row => row.Line.PlainText));
    }

    /// <summary>
    /// Known stack evidence colors bottom slots as types and the final slot as the top type with separate punctuation.
    /// </summary>
    [TestMethod]
    public async Task KnownStack_ColorsBottomSlotsAndTopIndependently()
    {
        await using var engine = new InProcessEngine();
        var state = await OpenAsync(engine, "ldc.i4.1\nldstr \"wrong\"\ncall int32 Math::Max(int32, int32)");
        var stack = Assert.ContainsSingle(state.Help!.Rows(512).Where(row => row.Line.PlainText
            == "Stack before (bottom → top): [int32, string]")).Line;

        Assert.AreSequenceEqual(
            [new TranscriptSpan("Stack before (bottom → top): ", SpanStyle.Dim), new("[", SpanStyle.Punctuation),
                new("int32", SpanStyle.Type), new(", ", SpanStyle.Punctuation), new("string", SpanStyle.TopType),
                new("]", SpanStyle.Punctuation)], stack.Spans);
    }

    /// <summary>
    /// An unknown predecessor does not hide the established stack or its concrete producer on a proven failing path.
    /// </summary>
    [TestMethod]
    public async Task UnknownPath_RetainsEstablishedStackAndProducer()
    {
        await using var engine = new InProcessEngine();
        string[] lines =
        [
            ".method void F(int32 flag) {", "ldarg flag", "brtrue UNKNOWN", "ldstr \"known\"", "br JOIN",
            "UNKNOWN:", "call MissingType::Missing()", "JOIN: call int32 Math::Abs(int32)", "pop", "ret", "}",
        ];
        var state = await OpenAsync(engine, string.Join('\n', lines), 7);
        var rows = state.Help!.Rows(512);
        var stack = Assert.ContainsSingle(rows.Where(row => row.Line.PlainText
            == "Stack before (bottom → top): unknown (established path: [string])")).Line;

        Assert.AreEqual("unknown", StyledText(stack, SpanStyle.Default));
        Assert.AreEqual("string", StyledText(stack, SpanStyle.TopType));
        Assert.AreEqual("[]", StyledText(stack, SpanStyle.Punctuation));
        Assert.Contains(row => row.Line.PlainText == "  from line 4: ldstr \"known\"", rows);
        Assert.DoesNotContain(row => row.Line.PlainText.StartsWith("  from line 7:", StringComparison.Ordinal), rows);
        AssertEvidenceMatchesFormatter(state);
    }

    /// <summary>
    /// An incompatible join retains both incoming stacks and the instructions that produced their values.
    /// </summary>
    [TestMethod]
    public async Task IncomingPaths_KeepEachStackAndProducerVisible()
    {
        await using var engine = new InProcessEngine();
        string[] lines =
        [
            ".method void F(int32 flag) {", "ldarg flag", "brtrue STRING", "ldc.i4.1", "br JOIN",
            "STRING: ldstr \"x\"", "JOIN: pop", "ret", "}",
        ];
        var state = await OpenAsync(engine, string.Join('\n', lines), 6);
        var rows = state.Help!.Rows(512);
        var incoming = rows.Where(row => row.Line.PlainText.StartsWith("Incoming from ", StringComparison.Ordinal)).ToArray();

        Assert.HasCount(2, incoming);
        var number = Assert.ContainsSingle(incoming.Where(row => row.Line.PlainText == "Incoming from line 5: br JOIN: [int32]"));
        var text = Assert.ContainsSingle(incoming.Where(row => row.Line.PlainText
            == "Incoming from line 6: STRING: ldstr \"x\": [string]"));
        Assert.AreEqual("int32", StyledText(number.Line, SpanStyle.TopType));
        Assert.AreEqual("string", StyledText(text.Line, SpanStyle.TopType));
        Assert.AreEqual("br", StyledText(number.Line, SpanStyle.Opcode));
        Assert.AreEqual("ldstr", StyledText(text.Line, SpanStyle.Opcode));
        Assert.AreEqual(SpanStyle.Dim, number.Line.Spans[0].Style);
        Assert.AreEqual(SpanStyle.Dim, text.Line.Spans[0].Style);
        Assert.Contains(row => row.Line.PlainText == "    from line 4: ldc.i4.1", rows);
        Assert.Contains(row => row.Line.PlainText == "    from line 6: STRING: ldstr \"x\"", rows);
        AssertEvidenceMatchesFormatter(state);
    }

    /// <summary>
    /// A generic arithmetic effect removes metadata alignment padding while retaining numeric slot counts.
    /// </summary>
    [TestMethod]
    public async Task GenericEffect_CollapsesMetadataPadding()
    {
        await using var engine = new InProcessEngine();
        var state = await OpenAsync(engine, "add");
        var effect = Assert.ContainsSingle(state.Help!.Rows(512).Where(row => row.Line.PlainText == "Stack effect: 1 1 → 1")).Line;

        Assert.AreSequenceEqual([new TranscriptSpan("Stack effect: ", SpanStyle.Dim), new("1 1", SpanStyle.Number),
            new(" → ", SpanStyle.Punctuation), new("1", SpanStyle.Number)], effect.Spans);
    }

    /// <summary>
    /// Commas inside nested generic arguments remain part of one stack slot instead of creating extra slot colors.
    /// </summary>
    [TestMethod]
    public async Task NestedGenericEffect_KeepsCommasInsideOneSlot()
    {
        await using var engine = new InProcessEngine();
        const string source = ".method void F(Dictionary<string, List<int32>> values) {\nldarg values\npop\nret\n}";
        var state = await OpenAsync(engine, source, 1);
        Assert.IsEmpty(state.Analysis!.Diagnostics);
        var effect = Assert.ContainsSingle(state.Help!.Rows(512).Where(row => row.Line.PlainText.StartsWith("Stack effect: ",
            StringComparison.Ordinal))).Line;

        Assert.AreEqual("Stack effect: [] → [Dictionary<string, List<int32>>]", effect.PlainText);
        Assert.AreEqual("Dictionary<string, List<int32>>", StyledText(effect, SpanStyle.TopType));
        Assert.AreEqual("", StyledText(effect, SpanStyle.Type));
        Assert.AreEqual("[] → []", StyledText(effect, SpanStyle.Punctuation));
    }

    /// <summary>
    /// An implicit entry description remains prose instead of being marked as an unknown opcode.
    /// </summary>
    [TestMethod]
    public async Task SyntheticEntry_RetainsDefaultProseColor()
    {
        await using var engine = new InProcessEngine();
        var state = await OpenAsync(engine, ".method void F() {\nLOOP: ldc.i4.1\nbr LOOP\n}", 1);
        var entry = Assert.ContainsSingle(state.Help!.Rows(512)
            .Where(row => row.Line.PlainText.Contains("implicit entry: method entry", StringComparison.Ordinal))).Line;

        Assert.Contains("method entry", StyledText(entry, SpanStyle.Default));
        Assert.AreEqual("", StyledText(entry, SpanStyle.Error));
        AssertEvidenceMatchesFormatter(state);
    }

    private async Task<PromptState> OpenAsync(InProcessEngine engine, string source, int? caretLine = null)
    {
        var lines = source.Split('\n');
        var line = caretLine ?? lines.Length - 1;
        var caret = lines.Take(line).Sum(text => text.Length + 1) + lines[line].Length;
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        state.SetText(source, caret);
        state.Analysis = await engine.AnalyzeAsync(new(lines, line, lines[line].Length, state.Editor.Document.Version),
            TestContext.CancellationToken);
        PromptHelp.Open(state, engine.Catalog);
        Assert.IsNotNull(state.Help);
        return state;
    }

    private static string StyledText(TranscriptLine line, SpanStyle style) =>
        string.Concat(line.Spans.Where(span => span.Style == style).Select(span => span.Text));

    private static void AssertEvidenceMatchesFormatter(PromptState state)
    {
        var diagnostic = PromptDiagnostics.Current(state)!;
        var evidence = state.Help!.Rows(512).Skip(1).TakeWhile(row => row.Action < 0 && row.Line.PlainText.Length > 0);
        Assert.AreSequenceEqual(DiagnosticFormatter.Details(diagnostic), evidence.Select(row => row.Line.PlainText));
    }

    private static void AssertAction(IReadOnlyList<(TranscriptLine Line, int Action)> rows, int index, string text, SpanStyle style)
    {
        var action = Assert.ContainsSingle(rows.Where(row => row.Action == index)).Line;
        Assert.AreEqual(text, action.PlainText);
        Assert.DoesNotContain(span => span.Style != style, action.Spans);
    }
}
