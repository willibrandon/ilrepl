using IlRepl.Batch;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies native inspection crosses the real host protocol and controls terminal batch exit behavior.
/// </summary>
[TestClass]
public sealed class NativeHostProcessTests
{
    /// <summary>
    /// Supplies cancellation to real host and inspection processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A pending cell crosses RPC as source, is inspected without execution, and remains available for explicit execution.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Rpc_PendingCellInspectionPreservesSourceAndExecutionBoundary()
    {
        using var files = new SessionWorkspaceFixture();
        var token = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(token);
        foreach (var line in files.PendingDocument().Entries.SelectMany(entry => entry.Source))
        {
            Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
        }

        var prepared = await engine.HandleAsync(".jit", token);
        Assert.IsTrue(prepared.Succeeded);
        Assert.IsNotNull(prepared.PendingNative);
        var before = engine.Status;

        var inspected = await engine.InspectNativeAsync(prepared.PendingNative.Identity, token);

        Assert.IsTrue(inspected.Succeeded, string.Join('\n', inspected.Lines.Select(line => line.PlainText)));
        Assert.IsNotNull(inspected.Native);
        Assert.AreEqual("complete", inspected.Native.Outcome, inspected.Native.Left.Detail);
        Assert.AreEqual("current cell", inspected.Native.Left.Name);
        Assert.AreEqual(0, inspected.Native.Left.Invocations);
        Assert.IsNotEmpty(Assert.ContainsSingle(inspected.Native.Left.Compilations).Listing);
        Assert.Contains(line => line.Kind == LineKind.Listing && line.Spans.Any(span => span.Style == SpanStyle.Opcode), inspected.Lines);
        Assert.AreEqual(before.CellNumber, engine.Status.CellNumber);
        Assert.AreEqual(before.Instructions, engine.Status.Instructions);
        Assert.IsFalse(engine.Status.CellIsEmpty);
        Assert.IsFalse(File.Exists(files.MarkerPath));
        await Assert.ThrowsAsync<ReplEngineException>(() => engine.InspectNativeAsync(prepared.PendingNative.Identity, token));
        var ran = await engine.HandleAsync("ret", token);
        Assert.IsTrue(ran.Succeeded);
        Assert.AreEqual("executed", await File.ReadAllTextAsync(files.MarkerPath, token));
        Assert.Contains(line => line.Kind == LineKind.Result
            && line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), ran.Lines);
    }

    /// <summary>
    /// Native equality assertions gate real batch exit status while ordinary differences remain successful inspections.
    /// </summary>
    /// <param name="change">Whether the second method contains a different observable constant.</param>
    /// <param name="assert">Whether the script requires native equality.</param>
    /// <param name="expectedExit">The terminal process exit contract.</param>
    [TestMethod]
    [DataRow(false, true, 0)]
    [DataRow(true, false, 0)]
    [DataRow(true, true, 1)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Batch_NativeAssertionControlsExitCode(bool change, bool assert, int expectedExit)
    {
        await using var engine = await HostPaths.StartEngineAsync(TestContext.CancellationToken);
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);
        string[] script =
        [
            ".method int32 Left() {", "ldc.i4 305419896", "ret", "}",
            ".method int32 Right() {", change ? "ldc.i4 305419897" : "ldc.i4 305419896", "ret", "}",
            ".jit Left --against Right" + (assert ? " --assert" : ""),
        ];

        var exit = await runner.RunAsync(script, TestContext.CancellationToken);

        Assert.AreEqual(expectedExit, exit, output.ToString());
        Assert.Contains("native inspection: " + (change ? "different" : "equal"), output.ToString());
        Assert.Contains("Left: complete", output.ToString());
        Assert.Contains("Right: complete", output.ToString());
        Assert.IsTrue(engine.Status.CellIsEmpty);
    }
}
