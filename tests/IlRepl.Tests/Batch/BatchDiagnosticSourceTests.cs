using IlRepl.Batch;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Batch;

/// <summary>
/// Checks real batch coordinates and honest provenance when callers supply no source document.
/// </summary>
[TestClass]
public sealed class BatchDiagnosticSourceTests
{
    /// <summary>
    /// Supplies cancellation for local and remote engine requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Comments and declarations count as script lines even when they do not create instructions.
    /// </summary>
    /// <param name="remote">Whether the batch runner uses the real host process.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BatchProducer_UsesPhysicalInputLine(bool remote)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = remote ? await HostPaths.StartEngineAsync(ct) : (IReplEngine)new InProcessEngine();
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);
        var code = await runner.RunAsync(["// header", ".locals init (int32 value)", "ldstr \"wrong\"",
            "call int32 Math::Abs(int32)"], ct);
        Assert.AreEqual(1, code);
        var text = output.ToString();
        Assert.Contains("argument 1: expected int32; actual string", text);
        Assert.Contains("from line 3: ldstr \"wrong\"", text);
        Assert.DoesNotContain("from line 2:", text);
    }

    /// <summary>
    /// A caller without source coordinates receives unavailable locations instead of instruction indices presented as lines.
    /// </summary>
    /// <param name="remote">Whether the requests cross the real host transport.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocationlessSubmission_DoesNotInventSourceLines(bool remote)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = remote ? await HostPaths.StartEngineAsync(ct) : (IReplEngine)new InProcessEngine();
        Assert.IsTrue((await engine.HandleAsync(".locals init (int32 value)", ct)).Succeeded);
        Assert.IsTrue((await engine.HandleAsync("ldstr \"wrong\"", ct)).Succeeded);
        var reply = await engine.HandleAsync("call int32 Math::Abs(int32)", ct);
        Assert.IsFalse(reply.Succeeded);
        var diagnostic = Assert.ContainsSingle(reply.Diagnostics.Where(item => item.Code == "FLOW005"));
        var facts = diagnostic.Explanation;
        Assert.IsNotNull(facts);
        var conflict = Assert.ContainsSingle(facts.Conflicts);
        var producer = Assert.ContainsSingle(conflict.Producers);
        Assert.AreEqual("string", conflict.Actual);
        Assert.AreEqual("ldstr \"wrong\"", producer.Source);
        Assert.AreEqual(-1, diagnostic.Location.Line);
        Assert.AreEqual(-1, producer.Location.Line);
        Assert.AreEqual(AnalysisSourceKind.Unavailable, producer.Kind);
        var details = string.Join('\n', DiagnosticFormatter.Details(diagnostic));
        Assert.Contains("source location unavailable: ldstr \"wrong\"", details);
        Assert.DoesNotContain("from line ", details);
    }
}
