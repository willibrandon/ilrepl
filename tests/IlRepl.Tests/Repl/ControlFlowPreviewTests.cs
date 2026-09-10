using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Checks the complete unsent document without executing definitions or changing the session.
/// </summary>
[TestClass]
public sealed class ControlFlowPreviewTests
{
    /// <summary>
    /// Supplies cancellation for asynchronous engine calls.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A later branch supplies the incoming stack at an earlier caret position.
    /// </summary>
    [TestMethod]
    public async Task Analyze_BackEdgeUsesSuffixAtCaret()
    {
        var session = new Session();
        using var editing = new EditingSession(session);
        string[] lines = [".method void Loop() {", "br LATER", "EARLIER: pop", "ret", "LATER: ldc.i4.1", "br EARLIER", "}"];
        var reply = await editing.AnalyzeAsync(new AnalysisRequest(lines, 2, 9, 1), TestContext.CancellationToken);
        Assert.IsNotNull(reply.Stack);
        Assert.AreEqual("[int32]", reply.Stack.Render());
        Assert.IsTrue(reply.BeforeInstruction);
        Assert.IsEmpty(session.Methods);
        Assert.IsTrue(session.Cell.IsEmpty);
    }

    /// <summary>
    /// A mismatched diamond retains both producers and a correction recomputes the graph.
    /// </summary>
    [TestMethod]
    public async Task Analyze_MismatchAndCorrectionKeepSourceLocations()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".method void F(int32 n) {", "  ldarg n", "  brfalse DONE", "  ldc.i4.1", "DONE: pop", "ret", "}"];
        var bad = await editing.AnalyzeAsync(new AnalysisRequest(lines, 4, 0, 1), TestContext.CancellationToken);
        var error = bad.Diagnostics.Single(d => d.Code == "FLOW003");
        Assert.AreEqual(4, error.Location.Line);
        Assert.Contains(location => location.Location.Line == 3, error.Related);
        lines = [".method void F(int32 n) {", "  ldarg n", "  brfalse DONE", "  ldc.i4.1", "  pop", "DONE: ret", "}"];
        var good = await editing.AnalyzeAsync(new AnalysisRequest(lines, 5, 0, 2), TestContext.CancellationToken);
        Assert.DoesNotContain(d => d.Kind == AnalysisDiagnosticKind.Error, good.Diagnostics);
        Assert.AreEqual("[]", good.Stack!.Render());
    }

    /// <summary>
    /// Read-only analysis identifies an unfinished operand and continues to later source.
    /// </summary>
    [TestMethod]
    public async Task Analyze_IncompleteOperandIsNotSkippedAsNop()
    {
        using var editing = new EditingSession(new Session());
        string[] lines = [".method void F() {", "call", "nop", "ret", "}"];
        var reply = await editing.AnalyzeAsync(new AnalysisRequest(lines, 2, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(AnalyzedStackKind.Unknown, reply.Stack!.Kind);
        Assert.Contains(d => d.Kind == AnalysisDiagnosticKind.Incomplete && d.Location.Line == 1, reply.Diagnostics);
        Assert.HasCount(1, reply.Diagnostics.Where(diagnostic => diagnostic.Location.Line == 1).ToArray());
    }

    /// <summary>
    /// Opcode prefixes remain incomplete without suggesting unrelated instructions, including after labels and comments.
    /// </summary>
    /// <param name="text">The partial source line.</param>
    [TestMethod]
    [DataRow("ldc")]
    [DataRow("ldc.")]
    [DataRow("ldc.i")]
    [DataRow("  START: ldc")]
    [DataRow("/* note */ FIRST: SECOND: ldc // note")]
    public async Task Analyze_OpcodePrefixDoesNotSuggestATypo(string text)
    {
        using var editing = new EditingSession(new Session());
        var reply = await editing.AnalyzeAsync(new AnalysisRequest([text, "nop"], 1, 0, 1), TestContext.CancellationToken);
        var diagnostic = reply.Diagnostics.Single(finding => finding.Location.Line == 0);
        Assert.AreEqual(AnalysisDiagnosticKind.Incomplete, diagnostic.Kind);
        Assert.DoesNotContain("did you mean", diagnostic.Message);
        Assert.DoesNotContain("unknown opcode", diagnostic.Message);
        Assert.Contains("Tab", diagnostic.Message);
        Assert.AreEqual(AnalyzedStackKind.Unknown, reply.Stack!.Kind);
    }

    /// <summary>
    /// A misspelling still suggests the intended instruction and disappears when corrected.
    /// </summary>
    [TestMethod]
    public async Task Analyze_OpcodeTypoStillSuggestsTheCorrection()
    {
        using var editing = new EditingSession(new Session());
        var bad = await editing.AnalyzeAsync(new AnalysisRequest(["lcd.i4 1"], 0, 8, 1), TestContext.CancellationToken);
        var diagnostic = bad.Diagnostics.Single();
        Assert.AreEqual(AnalysisDiagnosticKind.Error, diagnostic.Kind);
        Assert.Contains("did you mean 'ldc.i4'", diagnostic.Message);
        var corrected = await editing.AnalyzeAsync(new AnalysisRequest(["ldc.i4 1"], 0, 8, 2), TestContext.CancellationToken);
        Assert.IsEmpty(corrected.Diagnostics);
    }

    /// <summary>
    /// Object-only instructions accept reference-constrained parameters and reject unconstrained parameters.
    /// </summary>
    /// <param name="instruction">The object-only instruction.</param>
    [TestMethod]
    [DataRow("throw")]
    [DataRow("castclass object")]
    [DataRow("isinst object")]
    [DataRow("unbox int32")]
    [DataRow("unbox.any int32")]
    public async Task Analyze_ObjectOnlyInstructionRequiresReferenceGenericConstraint(string instruction)
    {
        var tail = instruction == "throw" ? "" : "\npop\nret";
        var accepted = $".class public Good {{\n.method public static void F<class T>(!!T value) {{\n"
            + $"ldarg value\n{instruction}{tail}\n}}\n}}";
        var rejected = $".class public Bad {{\n.method public static void F<T>(!!T value) {{\n"
            + $"ldarg value\n{instruction}{tail}\n}}\n}}";
        using var goodEditing = new EditingSession(new Session());
        var good = await goodEditing.AnalyzeAsync(new AnalysisRequest(accepted.Split('\n'), 2, 0, 1), TestContext.CancellationToken);
        Assert.DoesNotContain(finding => finding.Kind == AnalysisDiagnosticKind.Error, good.Diagnostics,
            string.Join("; ", good.Diagnostics.Select(finding => finding.Message)));
        using var badEditing = new EditingSession(new Session());
        var bad = await badEditing.AnalyzeAsync(new AnalysisRequest(rejected.Split('\n'), 2, 0, 2), TestContext.CancellationToken);
        Assert.Contains(finding => finding.Kind == AnalysisDiagnosticKind.Error
            && finding.Message.Contains("needs an object reference", StringComparison.Ordinal), bad.Diagnostics);
    }
}
