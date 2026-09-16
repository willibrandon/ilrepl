using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Checks producer attribution across control flow, accepted source, and analysis states.
/// </summary>
[TestClass]
public sealed class DiagnosticProvenanceTests
{
    /// <summary>
    /// Supplies cancellation for analysis and source submission.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Dup retains the original producer while a local load becomes the immediate producer of its loaded value.
    /// </summary>
    /// <param name="loadLocal">Whether a local load replaces dup in the source.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImmediateProducer_SurvivesDupAndIdentifiesLocalLoads(bool loadLocal)
    {
        await using var engine = new InProcessEngine();
        string[] lines = loadLocal
            ? [".locals init (string value)", "ldstr \"x\"", "stloc value", "ldloc value", "call int32 Math::Abs(int32)"]
            : ["ldstr \"x\"", "dup", "call int32 Math::Abs(int32)"];
        var reply = await engine.AnalyzeAsync(new(lines, lines.Length - 1, 0, 1), TestContext.CancellationToken);
        var facts = FactsAt(reply, lines.Length - 1);
        Assert.IsNotNull(facts.Stack);
        string[] expectedStack = loadLocal ? ["string"] : ["string", "string"];
        Assert.AreSequenceEqual(expectedStack, facts.Stack.Values);
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.AreEqual(loadLocal ? 0 : 1, conflict.Index);
        Assert.AreEqual("int32", conflict.Expected);
        Assert.AreEqual("string", conflict.Actual);
        var producer = Assert.ContainsSingle(conflict.Producers);
        Assert.AreEqual(loadLocal ? 3 : 0, producer.Location.Line);
        Assert.AreEqual(loadLocal ? "ldloc value" : "ldstr \"x\"", producer.Source);
    }

    /// <summary>
    /// The accepted fast path for dup keeps both original producers before any later diagnostic triggers a full rebuild.
    /// </summary>
    [TestMethod]
    public void IncrementalDup_PreservesOriginsBeforeAndAfterRebuild()
    {
        var session = new Session();
        session.AddLine("ldstr \"original\"");
        session.AddLine("dup");
        var appended = session.State.Analysis.End;
        Assert.IsNotNull(appended);
        Assert.IsNotNull(appended.Values);
        Assert.HasCount(2, appended.Values);
        Assert.AreSequenceEqual([0], appended.Values[0].Origins);
        Assert.AreSequenceEqual([0], appended.Values[1].Origins);
        session.AddLine("REBUILD:");
        var rebuilt = session.State.Analysis.End;
        Assert.IsNotNull(rebuilt);
        Assert.IsNotNull(rebuilt.Values);
        Assert.HasCount(2, rebuilt.Values);
        Assert.AreSequenceEqual([0], rebuilt.Values[0].Origins);
        Assert.AreSequenceEqual([0], rebuilt.Values[1].Origins);
    }

    /// <summary>
    /// Analysis of a new document after an accepted dup attributes its consumed value to the accepted original instruction.
    /// </summary>
    [TestMethod]
    public async Task AcceptedDup_AttributesNewDocumentFailureToOriginalSource()
    {
        await using var engine = new InProcessEngine();
        const string source = "ldstr \"original\"";
        var location = new AnalysisLocation("previous", 10, 0, source.Length);
        Assert.IsTrue((await engine.HandleSourceAsync(source, location, TestContext.CancellationToken)).Succeeded);
        Assert.IsTrue((await engine.HandleSourceAsync("dup", new("previous", 11, 0, 3), TestContext.CancellationToken)).Succeeded);
        var reply = await engine.AnalyzeAsync(new(["call int32 Math::Abs(int32)"], 0, 0, 1), TestContext.CancellationToken);
        var facts = FactsAt(reply, 0);
        Assert.AreEqual("[string, string]", facts.Stack!.Render());
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.AreEqual(1, conflict.Index);
        var producer = Assert.ContainsSingle(conflict.Producers);
        Assert.AreEqual(AnalysisSourceKind.Accepted, producer.Kind);
        Assert.AreEqual(location, producer.Location);
        Assert.AreEqual(source, producer.Source);
    }

    /// <summary>
    /// Distinct branch producers survive reference-type merging without replacing their original source text.
    /// </summary>
    [TestMethod]
    public async Task Diamond_RetainsDistinctProducersAfterReferenceMerge()
    {
        await using var engine = new InProcessEngine();
        string[] lines =
        [
            ".method void F(int32 flag) {", "ldarg flag", "brtrue STRING", "newobj instance void object::.ctor()",
            "br JOIN", "STRING: ldstr \"x\"", "JOIN: call int32 Math::Abs(int32)", "pop", "ret", "}",
        ];
        var reply = await engine.AnalyzeAsync(new(lines, 6, 0, 1), TestContext.CancellationToken);
        var facts = FactsAt(reply, 6);
        Assert.IsNotNull(facts.Stack);
        Assert.AreSequenceEqual(["object"], facts.Stack.Values);
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.AreEqual("object", conflict.Actual);
        Assert.AreEqual("int32", conflict.Expected);
        Assert.AreSequenceEqual([3, 5], conflict.Producers.Select(item => item.Location.Line).Order());
        Assert.AreEqual(lines[3], conflict.Producers.Single(item => item.Location.Line == 3).Source);
        Assert.AreEqual(lines[5], conflict.Producers.Single(item => item.Location.Line == 5).Source);
    }

    /// <summary>
    /// Incompatible joins retain each predecessor's stack and value producers separately.
    /// </summary>
    /// <param name="depthMismatch">Whether the first path supplies an empty stack instead of an integer.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IncompatibleJoin_ExplainsBothIncomingPaths(bool depthMismatch)
    {
        await using var engine = new InProcessEngine();
        string[] lines =
        [
            ".method void F(int32 flag) {", "ldarg flag", "brtrue STRING", depthMismatch ? "nop" : "ldc.i4.1",
            "br JOIN", "STRING: ldstr \"x\"", "JOIN: pop", "ret", "}",
        ];
        var reply = await engine.AnalyzeAsync(new(lines, 6, 0, 1), TestContext.CancellationToken);
        var diagnostic = Assert.ContainsSingle(reply.Diagnostics.Where(item => item.Code == "FLOW003"));
        Assert.AreEqual(6, diagnostic.Location.Line);
        var facts = diagnostic.Explanation;
        Assert.IsNotNull(facts);
        Assert.HasCount(2, facts.Incoming);
        var numeric = Assert.ContainsSingle(facts.Incoming.Where(path => path.Source.Location.Line == 4));
        var text = Assert.ContainsSingle(facts.Incoming.Where(path => path.Source.Location.Line == 5));
        Assert.AreEqual(AnalyzedStackKind.Known, numeric.Stack.Kind);
        Assert.AreEqual(depthMismatch ? "[]" : "[int32]", numeric.Stack.Render());
        Assert.AreEqual("[string]", text.Stack.Render());
        Assert.AreEqual(lines[4], numeric.Source.Source);
        Assert.AreEqual(lines[5], text.Source.Source);
        if (depthMismatch)
        {
            Assert.IsEmpty(numeric.Values);
        }
        else
        {
            var value = Assert.ContainsSingle(numeric.Values);
            Assert.AreEqual("int32", value.Actual);
            Assert.AreEqual(3, Assert.ContainsSingle(value.Producers).Location.Line);
        }
        var stringValue = Assert.ContainsSingle(text.Values);
        Assert.AreEqual("string", stringValue.Actual);
        Assert.AreEqual(5, Assert.ContainsSingle(stringValue.Producers).Location.Line);
    }

    /// <summary>
    /// Loop convergence adds the back-edge producer once instead of freezing evidence at the first visit.
    /// </summary>
    [TestMethod]
    public async Task Loop_CollectsConvergedOriginsWithoutDuplicates()
    {
        await using var engine = new InProcessEngine();
        string[] lines =
        [
            ".method void F(int32 repeat) {", "ldstr \"initial\"", "LOOP: ldarg repeat", "brfalse END", "pop",
            "ldstr \"loop\"", "br LOOP", "END: call int32 Math::Abs(int32)", "pop", "ret", "}",
        ];
        var reply = await engine.AnalyzeAsync(new(lines, 7, 0, 1), TestContext.CancellationToken);
        var facts = FactsAt(reply, 7);
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.AreEqual("string", conflict.Actual);
        Assert.AreSequenceEqual([1, 5], conflict.Producers.Select(item => item.Location.Line).Order());
        Assert.HasCount(2, conflict.Producers);
        Assert.AreSequenceEqual([lines[1], lines[5]], conflict.Producers.OrderBy(item => item.Location.Line).Select(item => item.Source));
    }

    /// <summary>
    /// Accepted source remains distinguishable from the new document and keeps its original location and source text.
    /// </summary>
    [TestMethod]
    public async Task AcceptedSource_RetainsIdentityOutsideCurrentDocument()
    {
        await using var engine = new InProcessEngine();
        const string prior = "ldstr \"earlier\"";
        var location = new AnalysisLocation("previous-document", 27, 0, prior.Length);
        Assert.IsTrue((await engine.HandleSourceAsync(prior, location, TestContext.CancellationToken)).Succeeded);
        var reply = await engine.AnalyzeAsync(new(["call int32 Math::Abs(int32)"], 0, 0, 1), TestContext.CancellationToken);
        var facts = FactsAt(reply, 0);
        var source = Assert.ContainsSingle(Assert.ContainsSingle(facts.Conflicts).Producers);
        Assert.AreEqual(AnalysisSourceKind.Accepted, source.Kind);
        Assert.AreEqual(location, source.Location);
        Assert.AreEqual(prior, source.Source);
    }

    /// <summary>
    /// An implicit catch entry is identified as synthetic rather than attributed to a user instruction that never pushed it.
    /// </summary>
    [TestMethod]
    public async Task CatchEntry_IdentifiesItsSyntheticExceptionProducer()
    {
        await using var engine = new InProcessEngine();
        string[] lines =
        [
            ".method void F() {", ".try {", "leave DONE", "} catch [System.Runtime]System.Exception {",
            "call int32 Math::Abs(int32)", "pop", "leave DONE", "}", "DONE: ret", "}",
        ];
        var reply = await engine.AnalyzeAsync(new(lines, 4, 0, 1), TestContext.CancellationToken);
        var facts = FactsAt(reply, 4);
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.Contains("Exception", conflict.Actual!);
        Assert.AreEqual("int32", conflict.Expected);
        var source = Assert.ContainsSingle(conflict.Producers);
        Assert.AreEqual(AnalysisSourceKind.Synthetic, source.Kind);
        Assert.AreEqual("exception-handler entry: " + lines[3], source.Source);
    }

    /// <summary>
    /// A synthesized return identifies its method-closing or cell-run boundary without describing an exception handler.
    /// </summary>
    /// <param name="runCell">Whether the implicit return belongs to a run command instead of a closing method brace.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImplicitReturn_IdentifiesItsSourceBoundary(bool runCell)
    {
        await using var engine = new InProcessEngine();
        string[] lines = runCell ? ["ldc.i4.1", "ldc.i4.2", ".run"]
            : [".method int32 F() {", "ldstr \"wrong\"", "}"];
        var reply = await engine.AnalyzeAsync(new(lines, 2, 0, 1), TestContext.CancellationToken);
        var facts = FactsAt(reply, 2);
        Assert.IsNotNull(facts.Instruction);
        Assert.AreEqual("ret", facts.Instruction.Mnemonic);
        Assert.AreEqual("ret", facts.Instruction.Syntax);
        Assert.IsNotNull(facts.Source);
        Assert.AreEqual(AnalysisSourceKind.Synthetic, facts.Source.Kind);
        Assert.AreEqual(2, facts.Source.Location.Line);
        Assert.AreEqual("implicit return: " + lines[2], facts.Source.Source);
    }

    /// <summary>
    /// An unresolved path does not erase an independently proven failure on another incoming path.
    /// </summary>
    [TestMethod]
    public async Task UnknownPath_PreservesKnownFailureEvidence()
    {
        await using var engine = new InProcessEngine();
        string[] lines =
        [
            ".method void F(int32 flag) {", "ldarg flag", "brtrue UNKNOWN", "ldstr \"known\"", "br JOIN",
            "UNKNOWN: call MissingType::Missing()", "JOIN: call int32 Math::Abs(int32)", "pop", "ret", "}",
        ];
        var reply = await engine.AnalyzeAsync(new(lines, 6, 0, 1), TestContext.CancellationToken);
        var facts = FactsAt(reply, 6);
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        Assert.AreEqual("int32", conflict.Expected);
        Assert.AreEqual("string", conflict.Actual);
        Assert.AreEqual(3, Assert.ContainsSingle(conflict.Producers).Location.Line);
        Assert.DoesNotContain(item => item.Location.Line == 5, conflict.Producers);
    }

    /// <summary>
    /// Unreachable instructions and unverifiable instructions retain their states without invented error evidence.
    /// </summary>
    [TestMethod]
    public async Task NonErrorStates_RemainDistinct()
    {
        await using var engine = new InProcessEngine();
        string[] dead = [".method void F() {", "br DONE", "pop", "DONE: ret", "}"];
        var unreachable = await engine.AnalyzeAsync(new(dead, 2, 0, 1), TestContext.CancellationToken);
        Assert.AreEqual(AnalyzedStackKind.Unreachable, unreachable.Stack!.Kind);
        Assert.DoesNotContain(item => item.Kind == AnalysisDiagnosticKind.Error, unreachable.Diagnostics);
        Assert.IsNotNull(unreachable.InstructionHelp);
        Assert.AreEqual("pop", unreachable.InstructionHelp.Mnemonic);
        string[] unsafeLines = ["ldstr \"value\"", "no. 4", "callvirt instance string object::ToString()"];
        var unverifiable = await engine.AnalyzeAsync(new(unsafeLines, 2, 0, 2), TestContext.CancellationToken);
        Assert.Contains(item => item.Kind == AnalysisDiagnosticKind.Unverifiable, unverifiable.Diagnostics);
        Assert.DoesNotContain(item => item.Kind == AnalysisDiagnosticKind.Error, unverifiable.Diagnostics);
        Assert.AreEqual(AnalyzedStackKind.Known, unverifiable.Stack!.Kind);
        Assert.AreEqual("[string]", unverifiable.Stack.Render());
    }

    private static DiagnosticExplanation FactsAt(AnalysisReply reply, int line)
    {
        var diagnostic = Assert.ContainsSingle(reply.Diagnostics.Where(item => item.Kind == AnalysisDiagnosticKind.Error
            && item.Location.Line == line));
        Assert.IsNotNull(diagnostic.Explanation);
        return diagnostic.Explanation;
    }
}
