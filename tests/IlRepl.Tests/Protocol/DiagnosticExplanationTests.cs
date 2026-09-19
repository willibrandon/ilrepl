using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Checks the actual incoming stack, rejected operand requirements, and source evidence for stack failures.
/// </summary>
[TestClass]
public sealed class DiagnosticExplanationTests
{
    /// <summary>
    /// Supplies cancellation for analysis and submission requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Supplies independently checked operand roles with their expected type, slot, and immediate source producer.
    /// </summary>
    public static IEnumerable<(string Source, string Role, int Index, string Expected, string Actual, int Producer)> TypedCases =>
    [
        ("ldstr \"x\"\ncall int32 Math::Abs(int32)", "argument 1", 0, "int32", "string", 0),
        ("ldc.i4.1\nldstr \"x\"\ncall int32 Math::Max(int32, int32)", "argument 2", 1, "int32", "string", 1),
        ("ldc.i4.1\ncallvirt instance int32 string::get_Length()", "receiver", 0, "string", "int32", 0),
        ("ldstr \"text\"\nldc.i4.1\ncallvirt instance bool string::StartsWith(string)",
            "argument 1", 1, "string", "int32", 1),
        (".method int32 F() {\nldstr \"x\"\nret", "return value", 0, "int32", "string", 1),
        (".locals init (int32 value)\nldstr \"x\"\nstloc value", "value", 0, "int32", "string", 1),
        (".method void F(int32 value) {\nldstr \"x\"\nstarg value", "value", 0, "int32", "string", 1),
        (".class DiagnosticField {\n.field public static int32 Value\n}\nldstr \"x\"\n"
            + "stsfld int32 DiagnosticField::Value", "value", 0, "int32", "string", 3),
        (".class DiagnosticFieldReceiver {\n.field public int32 Value\n}\nldc.i4.1\n"
            + "ldfld int32 DiagnosticFieldReceiver::Value", "receiver", 0, "DiagnosticFieldReceiver", "int32", 3),
        (".locals init (int32 value)\nldloca value\nldstr \"x\"\nstind.i4", "value", 1, "int32", "string", 2),
        ("ldc.i4.1\nnewarr int32\nldc.i4.0\nldstr \"x\"\nstelem.i4", "value", 2, "int32", "string", 3),
    ];

    /// <summary>
    /// Each rejected typed operand identifies the exact slot, expected and actual types, and instruction that supplied it.
    /// </summary>
    /// <param name="source">The source ending at the failing instruction.</param>
    /// <param name="role">The semantic operand role.</param>
    /// <param name="index">The bottom-based slot index.</param>
    /// <param name="expected">The required type.</param>
    /// <param name="givenType">The supplied type.</param>
    /// <param name="producer">The supplying source line.</param>
    [TestMethod]
    [DynamicData(nameof(TypedCases))]
    public async Task TypedOperand_ReportsExactConflictAndProducer(
        string source,
        string role,
        int index,
        string expected,
        string givenType,
        int producer)
    {
        await using var engine = new InProcessEngine();
        var lines = source.Split('\n');
        var reply = await engine.AnalyzeAsync(new(lines, lines.Length - 1, 0, 1), TestContext.CancellationToken);
        var diagnostic = ErrorAt(reply, lines.Length - 1);
        var explanation = diagnostic.Explanation;
        Assert.IsNotNull(explanation);
        Assert.IsNotNull(explanation.Instruction);
        Assert.AreEqual(lines[^1].Split(' ')[0], explanation.Instruction.Mnemonic);
        Assert.Contains(expected, explanation.Requirement);
        Assert.IsNotNull(explanation.Stack);
        Assert.AreEqual(AnalyzedStackKind.Known, explanation.Stack.Kind);
        Assert.AreEqual(givenType, explanation.Stack.Values[index]);
        var conflict = Assert.ContainsSingle(explanation.Conflicts);
        Assert.AreEqual(index, conflict.Index);
        Assert.AreEqual(role, conflict.Role);
        Assert.AreEqual(expected, conflict.Expected);
        Assert.AreEqual(givenType, conflict.Actual);
        var origin = Assert.ContainsSingle(conflict.Producers);
        Assert.AreEqual(AnalysisSourceKind.Document, origin.Kind);
        Assert.AreEqual(producer, origin.Location.Line);
        Assert.AreEqual(lines[producer], origin.Source);
        Assert.IsEmpty(explanation.Incoming);
    }

    /// <summary>
    /// An unboxed value-type receiver requires an address, including the narrower managed address required by constrained calls.
    /// </summary>
    /// <param name="constrained">Whether the receiver must match a constrained managed pointer.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValueTypeReceiver_ExplainsItsRequiredAddress(bool constrained)
    {
        await using var engine = new InProcessEngine();
        string[] lines = constrained
            ? ["ldc.i4.1", "constrained. int32", "callvirt instance string object::ToString()"]
            : ["ldc.i4.1", "call instance string int32::ToString()"];
        var reply = await engine.AnalyzeAsync(new(lines, lines.Length - 1, 0, 1), TestContext.CancellationToken);
        var diagnostic = ErrorAt(reply, lines.Length - 1);
        Assert.AreEqual("FLOW005", diagnostic.Code);
        var facts = diagnostic.Explanation;
        Assert.IsNotNull(facts);
        var expected = constrained ? "int32&" : "int32& or int32* or native int";
        Assert.AreEqual("receiver must be " + expected, facts.Requirement);
        Assert.IsNotNull(facts.Stack);
        Assert.AreSequenceEqual(["int32"], facts.Stack.Values);
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.AreEqual(0, conflict.Index);
        Assert.AreEqual("receiver", conflict.Role);
        Assert.AreEqual(expected, conflict.Expected);
        Assert.AreEqual("int32", conflict.Actual);
        var producer = Assert.ContainsSingle(conflict.Producers);
        Assert.AreEqual(AnalysisSourceKind.Document, producer.Kind);
        Assert.AreEqual(0, producer.Location.Line);
        Assert.AreEqual("ldc.i4.1", producer.Source);
    }

    /// <summary>
    /// Underflow retains the available stack and marks the missing operand without inventing a producer or type.
    /// </summary>
    /// <param name="empty">Whether both operands are missing.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Underflow_ExplainsMissingValueAndAvailableStack(bool empty)
    {
        await using var engine = new InProcessEngine();
        string[] lines = empty ? ["add"] : ["ldc.i4.1", "add"];
        var reply = await engine.AnalyzeAsync(new(lines, lines.Length - 1, 0, 1), TestContext.CancellationToken);
        var diagnostic = ErrorAt(reply, lines.Length - 1);
        Assert.AreEqual("FLOW006", diagnostic.Code);
        var explanation = diagnostic.Explanation;
        Assert.IsNotNull(explanation);
        Assert.IsNotNull(explanation.Stack);
        Assert.AreEqual(empty ? "[]" : "[int32]", explanation.Stack.Render());
        Assert.Contains("2", explanation.Requirement);
        var missing = Assert.ContainsSingle(explanation.Conflicts.Where(conflict => conflict.Actual is null));
        Assert.AreEqual(-1, missing.Index);
        Assert.AreEqual("missing value", missing.Role);
        Assert.Contains(empty ? "2" : "1", missing.Expected);
        Assert.IsEmpty(missing.Producers);
    }

    /// <summary>
    /// Array index errors identify the index independently from the array object and stored value.
    /// </summary>
    [TestMethod]
    public async Task ArrayIndex_ReportsTheIndexSlot()
    {
        await using var engine = new InProcessEngine();
        string[] lines = ["ldc.i4.1", "newarr int32", "ldstr \"index\"", "ldelem.i4"];
        var reply = await engine.AnalyzeAsync(new(lines, 3, 0, 1), TestContext.CancellationToken);
        var explanation = ErrorAt(reply, 3).Explanation;
        Assert.IsNotNull(explanation);
        Assert.IsNotNull(explanation.Stack);
        Assert.AreSequenceEqual(["int32[]", "string"], explanation.Stack.Values);
        var conflict = Assert.ContainsSingle(explanation.Conflicts);
        Assert.AreEqual(1, conflict.Index);
        Assert.AreEqual("index", conflict.Role);
        Assert.Contains("int", conflict.Expected);
        Assert.AreEqual("string", conflict.Actual);
        Assert.AreEqual(2, Assert.ContainsSingle(conflict.Producers).Location.Line);
        Assert.AreEqual(lines[2], conflict.Producers[0].Source);
    }

    /// <summary>
    /// Invalid binary operands retain both roles, their bottom-to-top order, and their independent producers.
    /// </summary>
    [TestMethod]
    public async Task BinaryOperands_ExplainBothSidesInStackOrder()
    {
        await using var engine = new InProcessEngine();
        string[] lines = ["ldstr \"x\"", "ldc.i4.1", "add"];
        var reply = await engine.AnalyzeAsync(new(lines, 2, 0, 1), TestContext.CancellationToken);
        var explanation = ErrorAt(reply, 2).Explanation;
        Assert.IsNotNull(explanation);
        Assert.IsNotNull(explanation.Stack);
        Assert.AreSequenceEqual(["string", "int32"], explanation.Stack.Values);
        Assert.HasCount(2, explanation.Conflicts);
        Assert.AreSequenceEqual(["left operand", "right operand"], explanation.Conflicts.Select(conflict => conflict.Role));
        Assert.AreSequenceEqual([0, 1], explanation.Conflicts.Select(conflict => conflict.Index));
        Assert.AreSequenceEqual(["string", "int32"], explanation.Conflicts.Select(conflict => conflict.Actual));
        for (var index = 0; index < 2; index++)
        {
            var producer = Assert.ContainsSingle(explanation.Conflicts[index].Producers);
            Assert.AreEqual(index, producer.Location.Line);
            Assert.AreEqual(lines[index], producer.Source);
        }
    }

    /// <summary>
    /// Incremental submission and complete editor analysis agree on the same concrete failure evidence.
    /// </summary>
    /// <param name="duplicate">Whether the value reaches the failing call through dup.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SubmissionAndEditing_AgreeOnRequirementAndProducers(bool duplicate)
    {
        await using var engine = new InProcessEngine();
        string[] lines = duplicate
            ? [".method int32 F() {", "// before producer", "ldstr \"wrong\"", "dup", "call int32 Math::Abs(int32)"]
            : [".method int32 F() {", "// before producer", "ldstr \"wrong\"", "call int32 Math::Abs(int32)"];
        var failedLine = lines.Length - 1;
        var preview = await engine.AnalyzeAsync(new(lines, failedLine, 0, 1), TestContext.CancellationToken);
        var editing = ErrorAt(preview, failedLine);
        for (var index = 0; index < failedLine; index++)
        {
            var accepted = await engine.HandleSourceAsync(lines[index], new("document", index, 0, lines[index].Length),
                TestContext.CancellationToken);
            Assert.IsTrue(accepted.Succeeded, string.Join('\n', accepted.Lines.Select(line => line.PlainText)));
        }

        var refused = await engine.HandleSourceAsync(lines[failedLine], new("document", failedLine, 0, lines[failedLine].Length),
            TestContext.CancellationToken);
        Assert.IsFalse(refused.Succeeded);
        var submitted = Assert.ContainsSingle(refused.Diagnostics.Where(item => item.Code == editing.Code));
        Assert.IsNotNull(editing.Explanation);
        Assert.IsNotNull(submitted.Explanation);
        Assert.AreEqual(editing.Explanation.Requirement, submitted.Explanation.Requirement);
        Assert.AreSequenceEqual(editing.Explanation.Stack!.Values, submitted.Explanation.Stack!.Values);
        var expected = Assert.ContainsSingle(editing.Explanation.Conflicts);
        var actual = Assert.ContainsSingle(submitted.Explanation.Conflicts);
        Assert.AreEqual(expected.Index, actual.Index);
        Assert.AreEqual(expected.Role, actual.Role);
        Assert.AreEqual(expected.Expected, actual.Expected);
        Assert.AreEqual(expected.Actual, actual.Actual);
        Assert.AreSequenceEqual(
            expected.Producers.Select(item => (item.Location.Line, item.Location.Start, item.Location.Length, item.Location.Offset)),
            actual.Producers.Select(item => (item.Location.Line, item.Location.Start, item.Location.Length, item.Location.Offset)));
        Assert.AreEqual("document", Assert.ContainsSingle(actual.Producers).Location.Body);
        Assert.AreSequenceEqual(expected.Producers.Select(item => item.Source), actual.Producers.Select(item => item.Source));
        var transcript = string.Join('\n', refused.Lines.Select(line => line.PlainText));
        Assert.Contains("Expected:", transcript);
        Assert.Contains(duplicate ? "[string, string]" : "[string]", transcript);
        Assert.Contains("ldstr \"wrong\"", transcript);
        Assert.ContainsSingle(refused.Lines.Where(line => line.PlainText.Contains(submitted.Message, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Correcting the operand removes the old failure and updates the analyzed stack without retaining stale evidence.
    /// </summary>
    [TestMethod]
    public async Task Correction_ClearsDiagnosticEvidence()
    {
        await using var engine = new InProcessEngine();
        var wrong = await engine.AnalyzeAsync(new(["ldstr \"x\"", "call int32 Math::Abs(int32)"], 1, 0, 1),
            TestContext.CancellationToken);
        Assert.IsNotNull(ErrorAt(wrong, 1).Explanation);
        var fixedReply = await engine.AnalyzeAsync(new(["ldc.i4.m1", "call int32 Math::Abs(int32)"], 1, 0, 2),
            TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.Kind == AnalysisDiagnosticKind.Error, fixedReply.Diagnostics);
        Assert.AreEqual(2, fixedReply.DocumentVersion);
        Assert.AreEqual("[int32]", fixedReply.Stack!.Render());
    }

    /// <summary>
    /// An invalid prefix target explains the structural rule without inventing conflicting stack operands.
    /// </summary>
    [TestMethod]
    public async Task PrefixFailure_ExplainsTheStructuralRequirement()
    {
        await using var engine = new InProcessEngine();
        var reply = await engine.AnalyzeAsync(new(["readonly.", "nop"], 1, 0, 1), TestContext.CancellationToken);
        var diagnostic = Assert.ContainsSingle(reply.Diagnostics.Where(item => item.Code == "FLOW019"));
        var facts = diagnostic.Explanation;
        Assert.IsNotNull(facts);
        Assert.Contains("readonly.", facts.Requirement);
        Assert.Contains("nop", facts.Requirement);
        Assert.IsEmpty(facts.Conflicts);
        Assert.IsEmpty(facts.Incoming);
        Assert.IsNotNull(facts.Instruction);
        Assert.AreEqual("readonly.", facts.Instruction.Mnemonic);
    }

    /// <summary>
    /// A tail call in a cell identifies the return value that would require boxing or an inserted null before ret.
    /// </summary>
    /// <param name="returnsValue">Whether the tail callee returns a value type instead of void.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TailReturn_ExplainsTheUnreturnableValue(bool returnsValue)
    {
        await using var engine = new InProcessEngine();
        string[] lines = returnsValue
            ? ["ldc.i4.s -42", "tail.", "call int32 Math::Abs(int32)", "ret"]
            : ["tail.", "call void GC::Collect()", "ret"];
        var reply = await engine.AnalyzeAsync(new(lines, lines.Length - 1, 0, 1), TestContext.CancellationToken);
        var facts = ErrorAt(reply, lines.Length - 1).Explanation;
        Assert.IsNotNull(facts);
        Assert.Contains("object", facts.Requirement);
        Assert.IsNotNull(facts.Stack);
        Assert.AreEqual(returnsValue ? "[int32]" : "[]", facts.Stack.Render());
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.AreEqual("return value", conflict.Role);
        Assert.Contains("object", conflict.Expected);
        Assert.AreEqual(returnsValue ? "int32" : null, conflict.Actual);
        Assert.AreEqual(returnsValue ? 0 : -1, conflict.Index);
        if (returnsValue)
        {
            var producer = Assert.ContainsSingle(conflict.Producers);
            Assert.AreEqual(2, producer.Location.Line);
            Assert.AreEqual(lines[2], producer.Source);
        }
        else
        {
            Assert.IsEmpty(conflict.Producers);
        }
    }

    private static AnalysisDiagnostic ErrorAt(AnalysisReply reply, int line) =>
        Assert.ContainsSingle(reply.Diagnostics.Where(item => item.Kind == AnalysisDiagnosticKind.Error && item.Location.Line == line));
}
