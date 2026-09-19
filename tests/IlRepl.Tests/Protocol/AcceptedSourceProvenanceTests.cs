using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Accepted sources retain their coordinates through session rebuilds and in a later editor document.
/// </summary>
[TestClass]
public sealed class AcceptedSourceProvenanceTests
{
    /// <summary>
    /// Supplies cancellation for real engine submissions and analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Previously submitted producers retain accepted identity and exact coordinates across body kinds and replay operations.
    /// </summary>
    /// <param name="context">The kind of open body that owns the producer.</param>
    /// <param name="replay">The operation that rebuilds accepted source before requesting analysis.</param>
    [TestMethod]
    [DataRow("cell", "none")]
    [DataRow("cell", "undo")]
    [DataRow("cell", "definition")]
    [DataRow("method", "none")]
    [DataRow("method", "undo")]
    [DataRow("member", "none")]
    [DataRow("member", "undo")]
    [DataRow("edit", "none")]
    public async Task AcceptedProducer_PreservesLocationAcrossSessionReplay(string context, string replay)
    {
        await using var engine = new InProcessEngine();
        string[] prefix = context switch
        {
            "cell" => [],
            "method" => [".method int32 Read() {"],
            "member" => [".class public Fixture {", ".method public static int32 Read() {"],
            _ => [".class public Fixture {", ".method public static int32 Read() {", "ldc.i4.1", "ret", "}", "}",
                ".edit int32 Fixture::Read() as Copy {", ".method public static int32 Read() cil managed {"],
        };

        foreach (var line in prefix)
        {
            var accepted = await engine.HandleAsync(line, TestContext.CancellationToken);
            Assert.IsTrue(accepted.Succeeded, string.Join('\n', accepted.Lines.Select(item => item.PlainText)));
        }

        const string comment = "// accepted comment before the producer";
        Assert.IsTrue((await engine.HandleSourceAsync(comment, new("accepted-" + context, 17, 0, comment.Length),
            TestContext.CancellationToken)).Succeeded);
        const string raw = "    ldstr \"earlier\"";
        var location = new AnalysisLocation("accepted-" + context, 18, 4, raw.Length - 4);
        Assert.IsTrue((await engine.HandleSourceAsync(raw, location, TestContext.CancellationToken)).Succeeded);
        string[] replayLines = replay switch
        {
            "undo" => ["nop", ".undo"],
            "definition" => [".method void Nothing() {", "ret", "}"],
            _ => [],
        };

        foreach (var line in replayLines)
        {
            var accepted = await engine.HandleAsync(line, TestContext.CancellationToken);
            Assert.IsTrue(accepted.Succeeded, string.Join('\n', accepted.Lines.Select(item => item.PlainText)));
        }

        var reply = await engine.AnalyzeAsync(new(["call int32 Math::Abs(int32)"], 0, 0, 1), TestContext.CancellationToken);

        var error = Assert.ContainsSingle(reply.Diagnostics.Where(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error));
        Assert.AreEqual("FLOW005", error.Code);
        Assert.AreEqual(0, error.Location.Line);
        Assert.IsNotNull(error.Explanation);
        var conflict = Assert.ContainsSingle(error.Explanation.Conflicts);
        Assert.AreEqual("int32", conflict.Expected);
        Assert.AreEqual("string", conflict.Actual);
        var producer = Assert.ContainsSingle(conflict.Producers);
        Assert.AreEqual(AnalysisSourceKind.Accepted, producer.Kind);
        Assert.AreEqual(location, producer.Location);
        Assert.AreEqual(raw.Trim(), producer.Source);
    }
}
