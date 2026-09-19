using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Verifies native completion produces executable selectors and preserves surrounding command arguments.
/// </summary>
[TestClass]
public sealed class NativeCompletionTests
{
    /// <summary>
    /// Supplies cancellation to real completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Named scenario completion inserts quoted metadata names and excludes methods requiring arguments.
    /// </summary>
    /// <param name="nativeDiff">Whether the scenario belongs to a native edit comparison.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_ScenarioPreservesQuotedNameAndTierOption(bool nativeDiff)
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Value(int32 value) { ldarg.0; ret }",
            ".method int32 'My Scenario'() { ldc.i4.s 42; call Value; ret }",
            ".method int32 'My Needs Argument'(int32 value) { ldarg.0; ret }");
        if (nativeDiff)
        {
            Submit(core, ".edit Value as Copy {", ".method public static int32 Value(int32 value) cil managed {",
                "ldarg.0", "ret", "}", "}");
        }

        await using var engine = new InProcessEngine(core);
        var start = (nativeDiff ? ".diff Copy --native" : ".jit Value") + " using ";
        var source = start + "MySuffix --tier tier1";

        var reply = await engine.CompleteAsync(new CompletionRequest([source], 0, start.Length + "My".Length, null, [], true),
            TestContext.CancellationToken);

        var candidate = Assert.ContainsSingle(reply.Items);
        Assert.AreEqual("'My Scenario'", candidate.InsertText);
        var accepted = source.Remove(reply.ReplaceStart, reply.ReplaceLength).Insert(reply.ReplaceStart, candidate.InsertText);
        Assert.AreEqual(start + "'My Scenario' --tier tier1", accepted);
        var captured = core.Handle(accepted);
        Assert.IsTrue(captured.Succeeded, string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText)));
        Assert.IsNotNull(captured.NativePackage);
        Assert.AreEqual("My Scenario", captured.NativePackage.Options.Scenario);
        Assert.AreEqual("tier1", captured.NativePackage.Options.Tier);
        Assert.IsTrue(captured.NativePackage.Options.Run);
        Assert.AreEqual(nativeDiff, captured.NativePackage.Right is not null);
    }

    /// <summary>
    /// Tier and profile value completions replace the entire partial value without changing the selected method.
    /// </summary>
    /// <param name="command">The native command and selector preceding the option.</param>
    /// <param name="option">The native option accepting enumerated values.</param>
    /// <param name="prefix">The typed partial value.</param>
    /// <param name="expected">The accepted value.</param>
    [TestMethod]
    [DataRow(".jit Value", "--tier", "full", "fullopts")]
    [DataRow(".jit Value", "--tier", "tier1", "tier1")]
    [DataRow(".jit Value", "--pgo", "of", "off")]
    [DataRow(".diff Copy --native", "--tier", "full", "fullopts")]
    [DataRow(".diff Copy --native", "--tier", "tier1", "tier1")]
    [DataRow(".diff Copy --native", "--pgo", "of", "off")]
    public async Task Complete_OptionValuePreservesRemainingCommand(string command, string option, string prefix, string expected)
    {
        await using var engine = new InProcessEngine();
        var start = command + " " + option + " ";
        var source = start + prefix + "Suffix --timeout 5s";

        var reply = await engine.CompleteAsync(new CompletionRequest([source], 0, start.Length + prefix.Length, null, [], true),
            TestContext.CancellationToken);

        var candidate = Assert.ContainsSingle(reply.Items);
        Assert.AreEqual(expected, candidate.InsertText);
        Assert.AreEqual(start.Length, reply.ReplaceStart);
        Assert.AreEqual(prefix.Length + "Suffix".Length, reply.ReplaceLength);
        Assert.AreEqual(start + expected + " --timeout 5s",
            source.Remove(reply.ReplaceStart, reply.ReplaceLength).Insert(reply.ReplaceStart, candidate.InsertText));
    }

    /// <summary>
    /// A comparison target completes independently of the first method and retains assertion mode.
    /// </summary>
    [TestMethod]
    public async Task Complete_AgainstSelectsSecondMethodWithoutReplacingFirst()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Original() { ldc.i4.1; ret }", ".method int32 Candidate() { ldc.i4.2; ret }");
        await using var engine = new InProcessEngine(core);
        const string source = ".jit Original --against CandSuffix --assert";

        var reply = await engine.CompleteAsync(new CompletionRequest([source], 0, ".jit Original --against Cand".Length, null, [], true),
            TestContext.CancellationToken);

        var candidate = Assert.ContainsSingle(reply.Items.Where(item => item.InsertText == "Candidate()"),
            string.Join(", ", reply.Items.Select(item => item.Name + "=" + item.InsertText)));
        var accepted = source.Remove(reply.ReplaceStart, reply.ReplaceLength).Insert(reply.ReplaceStart, candidate.InsertText);
        Assert.StartsWith(".jit Original --against ", accepted);
        Assert.EndsWith(" --assert", accepted);
        var captured = core.Handle(accepted);
        Assert.IsTrue(captured.Succeeded, string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText)));
        Assert.IsNotNull(captured.NativePackage);
        Assert.IsNotNull(captured.NativePackage.Right);
        Assert.Contains("Original", captured.NativePackage.Left.Method!.DisplayName);
        Assert.Contains("Candidate", captured.NativePackage.Right.Method!.DisplayName);
        Assert.IsTrue(captured.NativePackage.Options.Assert);
    }

    private static void Submit(ReplCore core, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n"
                + string.Join('\n', core.Transcript.Lines.Select(item => item.PlainText)));
        }
    }
}
