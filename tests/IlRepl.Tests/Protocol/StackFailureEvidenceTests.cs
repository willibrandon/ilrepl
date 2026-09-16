using Hex1b.Documents;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Checks that rejected stack shapes retain their values and navigable producers after graph convergence.
/// </summary>
[TestClass]
public sealed class StackFailureEvidenceTests
{
    /// <summary>
    /// Supplies cancellation for real engine analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A later predecessor preserves an established failure when incompatible and adds producers when compatible.
    /// </summary>
    /// <param name="incompatible">Whether the later path invalidates the join instead of contributing another string.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LaterPredecessor_PreservesMatchingFailureEvidence(bool incompatible)
    {
        await using var engine = new InProcessEngine();
        string[] lines =
        [
            ".method void F(int32 flag) {", "ldarg flag", "brtrue OTHER", "ldstr \"first\"", "br JOIN",
            "JOIN: call int32 Math::Abs(int32)", "pop", "ret", incompatible ? "OTHER: ldc.i4.1" : "OTHER: ldstr \"later\"",
            "br JOIN", "}",
        ];
        var reply = await engine.AnalyzeAsync(new(lines, 5, 0, 1), TestContext.CancellationToken);
        var diagnostic = Assert.ContainsSingle(reply.Diagnostics.Where(item => item.Code == "FLOW005" && item.Location.Line == 5));
        Assert.Contains("found string", diagnostic.Message);
        var facts = diagnostic.Explanation;
        Assert.IsNotNull(facts);
        Assert.IsNotNull(facts.Stack);
        Assert.AreEqual(AnalyzedStackKind.Known, facts.Stack.Kind);
        Assert.AreSequenceEqual(["string"], facts.Stack.Values);
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.AreEqual(0, conflict.Index);
        Assert.AreEqual("int32", conflict.Expected);
        Assert.AreEqual("string", conflict.Actual);
        int[] producers = incompatible ? [3] : [3, 8];
        Assert.AreSequenceEqual(producers, conflict.Producers.Select(producer => producer.Location.Line));
        Assert.AreSequenceEqual(producers.Select(line => lines[line]), conflict.Producers.Select(producer => producer.Source));
        Assert.IsTrue(conflict.Producers.All(producer => producer.Kind == AnalysisSourceKind.Document));
        Assert.AreEqual(incompatible, reply.Diagnostics.Any(item => item.Code == "FLOW003" && item.Location.Line == 5));
        var details = string.Join('\n', DiagnosticFormatter.Details(diagnostic));
        Assert.Contains("expected int32; actual string", details);
        Assert.DoesNotContain("actual missing", details);
    }

    /// <summary>
    /// Graph-level empty-stack failures identify every unwanted value and expose its producer as an editor action.
    /// </summary>
    /// <param name="backwardBranch">Whether the stack enters a backward-only branch target instead of a try region.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyStackRequirement_ExplainsAndNavigatesEveryProducer(bool backwardBranch)
    {
        await using var engine = new InProcessEngine();
        string[] lines = backwardBranch
            ? [".method void F() {", "br START", "TARGET: pop", "pop", "ret", "START: ldc.i4.1", "ldstr \"leftover\"",
                "br TARGET", "}"]
            : [".method void F() {", "ldc.i4.1", "ldstr \"leftover\"", ".try {", "leave DONE",
                "} catch [System.Runtime]System.Exception {", "pop", "leave DONE", "}", "DONE: ret", "}"];
        var target = backwardBranch ? 2 : 3;
        int[] producers = backwardBranch ? [5, 6] : [1, 2];
        var text = string.Join('\n', lines);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary));
        state.SetText(text, lines.Take(target).Sum(line => line.Length + 1));
        state.Analysis = await engine.AnalyzeAsync(new(lines, target, 0, state.Editor.Document.Version), TestContext.CancellationToken);
        var code = backwardBranch ? "FLOW017" : "FLOW016";
        var diagnostic = Assert.ContainsSingle(state.Analysis.Diagnostics.Where(item => item.Code == code));
        Assert.AreEqual(target, diagnostic.Location.Line);
        var facts = diagnostic.Explanation;
        Assert.IsNotNull(facts);
        Assert.Contains("empty stack", facts.Requirement);
        Assert.IsNotNull(facts.Stack);
        Assert.AreEqual(AnalyzedStackKind.Known, facts.Stack.Kind);
        Assert.AreSequenceEqual(["int32", "string"], facts.Stack.Values);
        Assert.HasCount(2, facts.Conflicts);
        for (var slot = 0; slot < facts.Conflicts.Count; slot++)
        {
            var conflict = facts.Conflicts[slot];
            Assert.AreEqual(slot, conflict.Index);
            Assert.AreEqual("an empty stack", conflict.Expected);
            Assert.AreEqual(slot == 0 ? "int32" : "string", conflict.Actual);
            var producer = Assert.ContainsSingle(conflict.Producers);
            Assert.AreEqual(producers[slot], producer.Location.Line);
            Assert.AreEqual(lines[producers[slot]], producer.Source);
            Assert.AreEqual(AnalysisSourceKind.Document, producer.Kind);
        }

        PromptHelp.Open(state, engine.Catalog);
        var help = state.Help;
        Assert.IsNotNull(help);
        Assert.AreSequenceEqual(producers, help.Actions.Where(action => action.Source is not null)
            .Select(action => action.Source!.Location.Line));
        var first = facts.Conflicts[0].Producers[0];
        help.Activate(state);
        Assert.IsNull(state.Help);
        Assert.AreEqual(text, state.Text);
        Assert.AreEqual(new DocumentPosition(first.Location.Line + 1, first.Location.Start + 1),
            state.Editor.Document.OffsetToPosition(state.Editor.Cursor.Position));
    }
}
