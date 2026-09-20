using IlRepl.Protocol;

namespace IlRepl.Tests.Repl;

/// <summary>
/// The published method-editing examples replay verbatim through RPC and execute their comparisons in real fresh processes.
/// </summary>
[TestClass]
public sealed class EditDocumentationTests
{
    /// <summary>
    /// Supplies cancellation for the host and comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The shipped transcript executes both comparisons and all cell results through the actual RPC host.
    /// </summary>
    [TestMethod]
    public async Task Transcript_ReplaysBothRevisionsComparisonsAndOriginalCalls()
    {
        await using var engine = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        var path = Path.Join(RepoPaths.Root, "samples", "Transcripts", "editing-methods.il");
        var comparisons = new List<ComparisonReply>();
        var returns = new List<string>();
        foreach (var line in await File.ReadAllLinesAsync(path, TestContext.CancellationToken))
        {
            var handled = await engine.HandleAsync(line, TestContext.CancellationToken);
            Assert.IsTrue(handled.Succeeded, line + "\n" + Transcript(handled));
            Assert.IsNull(handled.EditDocument, "The shipped transcript supplies both complete edit documents itself.");
            returns.AddRange(handled.Lines.Where(entry => entry.Kind == LineKind.Result).Select(entry => entry.PlainText));
            if (handled.PendingComparison is { } pending)
            {
                var executed = await engine.CompareAsync(pending.Identity, TestContext.CancellationToken);
                Assert.IsTrue(executed.Succeeded, Transcript(executed));
                Assert.IsNotNull(executed.Comparison);
                comparisons.Add(executed.Comparison);
            }
        }

        Assert.HasCount(3, returns);
        Assert.Contains("= 42 : int32", returns[0]);
        Assert.Contains("= 41 : int32", returns[1]);
        Assert.Contains("= 41 : int32", returns[2]);
        Assert.HasCount(2, comparisons);
        AssertDifferent(comparisons[0]);
        Assert.AreEqual("41", comparisons[0].Original.Result!.Value);
        Assert.AreEqual("42", comparisons[0].Edited.Result!.Value);
        Assert.AreEqual("match", comparisons[1].Outcome);
        Assert.AreEqual(2, comparisons[1].Revision);
        Assert.AreEqual("41", comparisons[1].Original.Result!.Value);
        Assert.AreEqual("41", comparisons[1].Edited.Result!.Value);
        Assert.HasCount(1, comparisons[1].Original.Invocations);
        Assert.HasCount(1, comparisons[1].Edited.Invocations);
        Assert.IsNull(engine.Status.OpenEdit);
        Assert.AreEqual(0, engine.Status.OpenDepth);
    }

    /// <summary>
    /// The generic example runs independently and binds each observed call to the selected string instantiation.
    /// </summary>
    [TestMethod]
    public async Task GenericScenario_RunsIndependentlyWithTheSelectedInstantiation()
    {
        await using var engine = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        var path = Path.Join(RepoPaths.Root, "docs", "src", "content", "docs", "usage", "editing-methods.md");
        var lines = await File.ReadAllLinesAsync(path, TestContext.CancellationToken);
        var scenario = lines.SkipWhile(line => line != ".class public Choice {").TakeWhile(line => line != "```").ToArray();
        Assert.IsNotEmpty(scenario);
        HandleReply? last = null;
        foreach (var line in scenario)
        {
            last = await engine.HandleAsync(line, TestContext.CancellationToken);
            Assert.IsTrue(last.Succeeded, line + "\n" + Transcript(last));
        }

        last = Required.Value(last, "The last reply");
        Assert.IsNotNull(last.PendingComparison);
        var executed = await engine.CompareAsync(last.PendingComparison.Identity, TestContext.CancellationToken);
        Assert.IsTrue(executed.Succeeded, Transcript(executed));
        Assert.IsNotNull(executed.Comparison);
        var comparison = executed.Comparison;
        AssertDifferent(comparison);
        Assert.AreEqual("first", comparison.Original.Result!.Value);
        Assert.AreEqual("second", comparison.Edited.Result!.Value);
        foreach (var inputs in new[] { comparison.Original, comparison.Edited }.Select(side => side.Invocations.Single().Inputs))
        {
            var first = inputs.Single(member => member.Name == "argument 0").Value;
            var second = inputs.Single(member => member.Name == "argument 1").Value;
            Assert.AreEqual("[System.Private.CoreLib]System.String", first.Type);
            Assert.AreEqual("first", first.Value);
            Assert.AreEqual("[System.Private.CoreLib]System.String", second.Type);
            Assert.AreEqual("second", second.Value);
        }
    }

    /// <summary>
    /// Every CIL block in the editing guide executes in document order and produces the exact documented observations.
    /// </summary>
    [TestMethod]
    public async Task Guide_ReplaysAllFencesAndMatchesDocumentedResults()
    {
        await using var engine = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        var path = Path.Join(RepoPaths.Root, "docs", "src", "content", "docs", "usage", "editing-methods.md");
        var lines = await File.ReadAllLinesAsync(path, TestContext.CancellationToken);
        var inside = false;
        var comparisons = new Dictionary<string, ComparisonReply>(StringComparer.Ordinal);
        var returns = new List<TranscriptLine>();
        var hydrated = new List<string>();
        var blocks = 0;
        foreach (var line in lines)
        {
            if (line == "```cil")
            {
                Assert.IsFalse(inside);
                inside = true;
                blocks++;
            }
            else if (line == "```" && inside)
            {
                inside = false;
            }
            else if (inside)
            {
                var result = await engine.HandleAsync(line, TestContext.CancellationToken);
                Assert.IsTrue(result.Succeeded, line + "\n" + Transcript(result));
                returns.AddRange(result.Lines.Where(entry => entry.Kind == LineKind.Result));
                if (result.EditDocument is { } document)
                {
                    hydrated.Add(document.Name);
                    foreach (var source in document.Source.Split('\n'))
                    {
                        var accepted = await engine.HandleAsync(source, TestContext.CancellationToken);
                        Assert.IsTrue(accepted.Succeeded, source + "\n" + Transcript(accepted));
                    }
                }

                if (result.PendingComparison is { } pending)
                {
                    var executed = await engine.CompareAsync(pending.Identity, TestContext.CancellationToken);
                    Assert.IsTrue(executed.Succeeded, Transcript(executed));
                    Assert.IsNotNull(executed.Comparison);
                    comparisons.Add(executed.Comparison.Name, executed.Comparison);
                }
            }
        }

        Assert.IsFalse(inside, "The guide must close its CIL fences.");
        Assert.AreEqual(7, blocks);
        Assert.HasCount(2, hydrated);
        Assert.Contains("Max_Edit", hydrated);
        Assert.Contains("Maximum", hydrated);
        Assert.HasCount(1, returns);
        Assert.Contains("= 42 : int32", returns[0].PlainText);
        Assert.HasCount(4, comparisons);
        var incremented = comparisons["Incremented"];
        AssertDifferent(incremented);
        Assert.AreEqual("41", incremented.Original.Result!.Value);
        Assert.AreEqual("42", incremented.Edited.Result!.Value);
        var step = comparisons["Step"];
        AssertDifferent(step);
        Assert.AreEqual("1", step.Original.Result!.Value);
        Assert.AreEqual("2", step.Edited.Result!.Value);
        AssertReceiver(step.Original.Invocations.Single().Inputs, "0");
        AssertReceiver(step.Edited.Invocations.Single().Inputs, "0");
        AssertReceiver(step.Original.Invocations.Single().Outputs, "1");
        AssertReceiver(step.Edited.Invocations.Single().Outputs, "2");
        var generic = comparisons["Second"];
        AssertDifferent(generic);
        Assert.AreEqual("[System.Private.CoreLib]System.String", generic.Original.Result!.Type);
        Assert.AreEqual("first", generic.Original.Result.Value);
        Assert.AreEqual("[System.Private.CoreLib]System.String", generic.Edited.Result!.Type);
        Assert.AreEqual("second", generic.Edited.Result.Value);
        var divided = comparisons["SafeDivide"];
        AssertDifferent(divided);
        Assert.IsNotNull(divided.Original.Exception);
        Assert.EndsWith("System.DivideByZeroException", divided.Original.Exception.Type);
        Assert.IsNull(divided.Edited.Exception);
        Assert.AreEqual("42", divided.Edited.Result!.Value);
        Assert.IsNull(engine.Status.OpenEdit);
        Assert.AreEqual(0, engine.Status.OpenDepth);

        var maximum = await engine.HandleAsync(".compare Maximum (17, 42) --assert", TestContext.CancellationToken);
        Assert.IsNotNull(maximum.PendingComparison);
        var matched = await engine.CompareAsync(maximum.PendingComparison.Identity, TestContext.CancellationToken);
        Assert.IsTrue(matched.Succeeded, Transcript(matched));
        Assert.AreEqual("match", matched.Comparison!.Outcome);
        Assert.AreEqual("42", matched.Comparison.Original.Result!.Value);
        Assert.AreEqual("42", matched.Comparison.Edited.Result!.Value);
        foreach (var source in new[] { "ldc.i4 41", "call Identity", "ret" })
        {
            var original = await engine.HandleAsync(source, TestContext.CancellationToken);
            Assert.IsTrue(original.Succeeded, Transcript(original));
            if (source == "ret")
            {
                Assert.Contains(entry => entry.Kind == LineKind.Result
                    && entry.PlainText.Contains("= 41 : int32", StringComparison.Ordinal), original.Lines);
            }
        }
    }

    private static void AssertDifferent(ComparisonReply comparison)
    {
        Assert.AreEqual("different", comparison.Outcome, comparison.Original.Detail + "; " + comparison.Edited.Detail);
        Assert.AreEqual(1, comparison.Revision);
        Assert.AreEqual("completed", comparison.Original.Outcome);
        Assert.AreEqual("completed", comparison.Edited.Outcome);
        Assert.HasCount(1, comparison.Original.Invocations);
        Assert.HasCount(1, comparison.Edited.Invocations);
    }

    private static void AssertReceiver(IReadOnlyList<ObservedMember> values, string expected)
    {
        var receiver = values.Single(member => member.Name == "receiver").Value;
        Assert.AreEqual("object", receiver.Kind);
        var field = receiver.Members.Single(member => member.Name.EndsWith("::Value", StringComparison.Ordinal)).Value;
        Assert.AreEqual("[System.Private.CoreLib]System.Int32", field.Type);
        Assert.AreEqual(expected, field.Value);
    }

    private static string Transcript(HandleReply reply) => string.Join("\n", reply.Lines.Select(line => line.PlainText));
}
