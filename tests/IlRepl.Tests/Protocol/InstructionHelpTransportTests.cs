using System.Text.Json;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Preserves instruction explanations and diagnostic evidence through the real engine transports and JSON contract.
/// </summary>
[TestClass]
public sealed class InstructionHelpTransportTests
{
    /// <summary>
    /// Supplies cancellation for host startup and engine requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Nested diagnostic facts keep their slot order, original types, locations, and source kinds through JSON.
    /// </summary>
    [TestMethod]
    public void Analysis_RoundTripsEveryExplanationComponent()
    {
        var instruction = new InstructionHelp("call", "call int32 Math::Abs(int32)", "[int32] → [int32]",
            "Calls the specified method directly.", ["Arguments are passed in declaration order."],
            "https://ilrepl.dev/reference/opcodes/#call");
        var producer = new AnalysisSource(new("document", 3, 2, 9), "  ldstr \"x\"", AnalysisSourceKind.Document);
        var accepted = new AnalysisSource(new("cell:1", 12, 0, 8), "ldc.i4.1", AnalysisSourceKind.Accepted);
        var imported = new AnalysisSource(new("Fixture::M", -1, 0, 6, 17), "ldnull", AnalysisSourceKind.Imported);
        var synthetic = new AnalysisSource(new("handler", -1, 0, 0), "exception handler entry", AnalysisSourceKind.Synthetic);
        var conflict = new StackConflict(1, "argument 1", "int32", "string", [producer, accepted, imported, synthetic]);
        var missing = new StackConflict(-1, "argument 2", "int32", null, []);
        var explanation = new DiagnosticExplanation(instruction, "argument 1 needs int32",
            new(AnalyzedStackKind.Known, ["object", "string"]), [conflict, missing],
            [new(accepted, new(AnalyzedStackKind.Known, ["int32"]), [conflict])]) { Source = imported };
        var diagnostic = new AnalysisDiagnostic("FLOW005", AnalysisDiagnosticKind.Error, "argument mismatch",
            new("document", 4, 0, 28), []) { Explanation = explanation };
        var reply = new AnalysisReply(4, 5, 6, 7, new(AnalyzedStackKind.Invalid, []), true, [diagnostic])
        {
            InstructionHelp = instruction,
        };

        var json = JsonSerializer.Serialize(reply, ProtocolJsonContext.Default.AnalysisReply);
        var restored = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.AnalysisReply);

        Assert.IsNotNull(restored);
        AssertHelp(instruction, restored.InstructionHelp);
        var facts = Assert.ContainsSingle(restored.Diagnostics).Explanation;
        Assert.IsNotNull(facts);
        AssertHelp(instruction, facts.Instruction);
        Assert.AreEqual(imported, facts.Source);
        Assert.AreEqual("argument 1 needs int32", facts.Requirement);
        Assert.IsNotNull(facts.Stack);
        Assert.AreEqual(AnalyzedStackKind.Known, facts.Stack.Kind);
        Assert.AreSequenceEqual(["object", "string"], facts.Stack.Values);
        Assert.HasCount(2, facts.Conflicts);
        var slot = facts.Conflicts[0];
        Assert.AreEqual(1, slot.Index);
        Assert.AreEqual("argument 1", slot.Role);
        Assert.AreEqual("int32", slot.Expected);
        Assert.AreEqual("string", slot.Actual);
        Assert.AreSequenceEqual(new[] { producer, accepted, imported, synthetic }, slot.Producers);
        Assert.AreEqual(-1, facts.Conflicts[1].Index);
        Assert.AreEqual("argument 2", facts.Conflicts[1].Role);
        Assert.AreEqual("int32", facts.Conflicts[1].Expected);
        Assert.IsNull(facts.Conflicts[1].Actual);
        Assert.IsEmpty(facts.Conflicts[1].Producers);
        var path = Assert.ContainsSingle(facts.Incoming);
        Assert.AreEqual(accepted, path.Source);
        Assert.AreEqual(AnalyzedStackKind.Known, path.Stack.Kind);
        Assert.AreSequenceEqual(["int32"], path.Stack.Values);
        Assert.AreEqual(1, Assert.ContainsSingle(path.Values).Index);
        Assert.AreEqual("argument 1", path.Values[0].Role);
        Assert.AreEqual("int32", path.Values[0].Expected);
        Assert.AreEqual("string", path.Values[0].Actual);
        Assert.AreSequenceEqual(slot.Producers, path.Values[0].Producers);
    }

    /// <summary>
    /// Older replies remain readable and unpopulated additions stay absent from their serialized representation.
    /// </summary>
    [TestMethod]
    public void LegacyReplies_KeepOptionalHelpAbsent()
    {
        const string json = """
            {"documentVersion":1,"revision":2,"bindingEpoch":3,"assemblyVersion":4,"beforeInstruction":false,
            "diagnostics":[{"code":"FLOW008","kind":0,"message":"finish instruction",
            "location":{"body":"document","line":0,"start":0,"length":2},"related":[]}]}
            """;
        var reply = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.AnalysisReply);
        Assert.IsNotNull(reply);
        Assert.IsNull(reply.InstructionHelp);
        Assert.IsNull(Assert.ContainsSingle(reply.Diagnostics).Explanation);
        var serialized = JsonSerializer.Serialize(reply, ProtocolJsonContext.Default.AnalysisReply);
        Assert.DoesNotContain("instructionHelp", serialized);
        Assert.DoesNotContain("explanation", serialized);

        const string itemJson = "{\"name\":\"call\",\"detail\":\"\",\"description\":\"call\",\"takesOperand\":true}";
        var item = JsonSerializer.Deserialize(itemJson, ProtocolJsonContext.Default.CompletionItem);
        Assert.IsNotNull(item);
        Assert.IsNull(item.InstructionHelp);
        Assert.AreEqual(itemJson, JsonSerializer.Serialize(item, ProtocolJsonContext.Default.CompletionItem));
    }

    /// <summary>
    /// Both engine transports deliver the same resolved instruction help and concrete producer evidence.
    /// </summary>
    [TestMethod]
    public async Task Engines_AgreeOnHelpAndDiagnosticEvidence()
    {
        await using var local = new InProcessEngine();
        await using var remote = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        string[] lines = ["ldstr \"x\"", "call int32 Math::Abs(int32)"];
        var request = new AnalysisRequest(lines, 1, lines[1].Length, 42);
        var left = await local.AnalyzeAsync(request, TestContext.CancellationToken);
        var right = await remote.AnalyzeAsync(request, TestContext.CancellationToken);

        Assert.IsNotNull(left.InstructionHelp);
        Assert.AreEqual("call", left.InstructionHelp.Mnemonic);
        AssertHelp(left.InstructionHelp, right.InstructionHelp);
        var localError = Assert.ContainsSingle(left.Diagnostics.Where(item => item.Kind == AnalysisDiagnosticKind.Error));
        var remoteError = Assert.ContainsSingle(right.Diagnostics.Where(item => item.Kind == AnalysisDiagnosticKind.Error));
        Assert.IsNotNull(localError.Explanation);
        Assert.IsNotNull(remoteError.Explanation);
        Assert.AreEqual(localError.Code, remoteError.Code);
        Assert.AreEqual(localError.Message, remoteError.Message);
        Assert.AreEqual(localError.Location, remoteError.Location);
        Assert.AreEqual(localError.Explanation.Source, remoteError.Explanation.Source);
        Assert.AreEqual(localError.Explanation.Requirement, remoteError.Explanation.Requirement);
        Assert.AreEqual(localError.Explanation.Stack!.Kind, remoteError.Explanation.Stack!.Kind);
        Assert.AreSequenceEqual(localError.Explanation.Stack!.Values, remoteError.Explanation.Stack!.Values);
        var localSlot = Assert.ContainsSingle(localError.Explanation.Conflicts);
        var remoteSlot = Assert.ContainsSingle(remoteError.Explanation.Conflicts);
        Assert.AreEqual("int32", localSlot.Expected);
        Assert.AreEqual("string", localSlot.Actual);
        Assert.AreEqual(localSlot.Index, remoteSlot.Index);
        Assert.AreEqual(localSlot.Role, remoteSlot.Role);
        Assert.AreEqual(localSlot.Expected, remoteSlot.Expected);
        Assert.AreEqual(localSlot.Actual, remoteSlot.Actual);
        Assert.AreSequenceEqual(localSlot.Producers, remoteSlot.Producers);
        Assert.AreEqual(0, Assert.ContainsSingle(remoteSlot.Producers).Location.Line);
        Assert.AreEqual(lines[0], remoteSlot.Producers[0].Source);

        const string prefix = "call Math::Ab";
        var completion = new CompletionRequest([prefix], 0, prefix.Length, null, []);
        var localItems = await local.CompleteAsync(completion, TestContext.CancellationToken);
        var remoteItems = await remote.CompleteAsync(completion, TestContext.CancellationToken);
        Assert.IsNotEmpty(localItems.Items);
        Assert.AreSequenceEqual(localItems.Items.Select(item => item.InsertText), remoteItems.Items.Select(item => item.InsertText));
        for (var index = 0; index < localItems.Items.Count; index++)
        {
            var localHelp = localItems.Items[index].InstructionHelp;
            Assert.IsNotNull(localHelp);
            AssertHelp(localHelp, remoteItems.Items[index].InstructionHelp);
        }
    }

    private static void AssertHelp(InstructionHelp expected, InstructionHelp? actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Mnemonic, actual.Mnemonic);
        Assert.AreEqual(expected.Syntax, actual.Syntax);
        Assert.AreEqual(expected.StackEffect, actual.StackEffect);
        Assert.AreEqual(expected.Explanation, actual.Explanation);
        Assert.AreSequenceEqual(expected.Notes, actual.Notes);
        Assert.AreEqual(expected.DocumentationUrl, actual.DocumentationUrl);
    }
}
