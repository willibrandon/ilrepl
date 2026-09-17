using IlRepl.Batch;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Batch;

/// <summary>
/// Verifies that batch input executes only newly submitted source and treats session commands as document operations.
/// </summary>
[TestClass]
public sealed class SessionBatchTests
{
    /// <summary>
    /// Supplies cancellation to real engine and filesystem operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Recalling a saved cell prints its editable source without executing it, including when command echo is disabled.
    /// </summary>
    /// <param name="remote">Whether requests cross the desktop host transport.</param>
    /// <param name="echo">Whether ordinary input is echoed.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Recall_PrintsDraftWithoutExecuting(bool remote, bool echo)
    {
        using var files = new SessionWorkspaceFixture();
        var document = files.CompletedDocument();
        File.Delete(files.MarkerPath);
        await using var engine = await StartAsync(remote);
        await HydrateAsync(engine, document);
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: echo);

        var result = await runner.RunAsync([".session cell 1"], TestContext.CancellationToken);

        Assert.AreEqual(0, result, output.ToString());
        Assert.Contains("editor draft (not executed)", output.ToString());
        Assert.Contains(string.Join(Environment.NewLine, engine.Editor.Lines), output.ToString());
        Assert.Contains("ldc.i4.s 42", output.ToString());
        Assert.IsFalse(File.Exists(files.MarkerPath));
    }

    /// <summary>
    /// A failed session action preserves its command echo, diagnostic, and the active accepted source.
    /// </summary>
    /// <param name="remote">Whether requests cross the desktop host transport.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SessionFailure_PreservesCommandEchoAndSource(bool remote)
    {
        await using var engine = await StartAsync(remote);
        Assert.IsTrue((await engine.HandleAsync("ldc.i4.7", TestContext.CancellationToken)).Succeeded);
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: true);

        var result = await runner.RunAsync([".session cell 99"], TestContext.CancellationToken);

        Assert.AreEqual(1, result);
        Assert.Contains("il[1]> .session cell 99", output.ToString());
        Assert.Contains("no retained cell 99", output.ToString());
        Assert.DoesNotContain("= 7 : int32", output.ToString());
        Assert.IsFalse(engine.Status.CellIsEmpty);
    }

    /// <summary>
    /// Empty input, comments and inspection leave a reopened executable buffer untouched at end of input.
    /// </summary>
    /// <param name="remote">Whether the engine runs in a real host process.</param>
    /// <param name="input">The physical input following reopening.</param>
    [TestMethod]
    [DataRow(false, "")]
    [DataRow(true, "")]
    [DataRow(false, "// inspect only\n.session\n.show")]
    [DataRow(true, "// inspect only\n.session\n.show")]
    [DataRow(false, "/* first line\nstill a comment */\n.session cells")]
    [DataRow(true, "/* first line\nstill a comment */\n.session cells")]
    public async Task ReopenedBuffer_AdministrativeInputDoesNotRunAtEof(bool remote, string input)
    {
        using var files = new SessionWorkspaceFixture();
        await using var engine = await StartAsync(remote);
        await HydrateAsync(engine, files.PendingDocument());
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);

        var code = await runner.RunAsync(input.Length == 0 ? [] : input.Split('\n'), TestContext.CancellationToken);

        Assert.AreEqual(0, code, output.ToString());
        Assert.IsFalse(File.Exists(files.MarkerPath), output.ToString());
        Assert.IsFalse(engine.Status.CellIsEmpty, "Reopening and inspection must retain the accepted body.");
        Assert.DoesNotContain("= 42 : int32", output.ToString());
    }

    /// <summary>
    /// A completed new definition does not turn restored instructions into an implicit execution request.
    /// </summary>
    /// <param name="remote">Whether requests cross the host transport.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReopenedBuffer_NewDefinitionDoesNotRunPreviousCellAtEof(bool remote)
    {
        using var files = new SessionWorkspaceFixture();
        await using var engine = await StartAsync(remote);
        await HydrateAsync(engine, files.PendingDocument());
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);

        var code = await runner.RunAsync([".method int32 Other() {", "ldc.i4.7", "ret", "}"],
            TestContext.CancellationToken);

        Assert.AreEqual(0, code, output.ToString());
        Assert.Contains("end of method Other", output.ToString());
        Assert.IsFalse(File.Exists(files.MarkerPath), output.ToString());
        Assert.IsFalse(engine.Status.CellIsEmpty);
    }

    /// <summary>
    /// New executable input retains the existing batch convention of running the resulting cell at end of input.
    /// </summary>
    /// <param name="remote">Whether requests cross the host transport.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReopenedBuffer_NewInstructionRunsAtEof(bool remote)
    {
        using var files = new SessionWorkspaceFixture();
        await using var engine = await StartAsync(remote);
        await HydrateAsync(engine, files.PendingDocument());
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);

        var code = await runner.RunAsync(["nop"], TestContext.CancellationToken);

        Assert.AreEqual(0, code, output.ToString());
        Assert.AreEqual("executed", await File.ReadAllTextAsync(files.MarkerPath, TestContext.CancellationToken));
        Assert.Contains("= 42 : int32", output.ToString());
        Assert.IsTrue(engine.Status.CellIsEmpty);
    }

    /// <summary>
    /// Instructions entered entirely inside a top-level protected block still execute when batch input ends.
    /// </summary>
    /// <param name="remote">Whether requests cross the host transport.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedBlock_NewInstructionsRunAtEof(bool remote)
    {
        await using var engine = await StartAsync(remote);
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);

        var code = await runner.RunAsync([
            ".try {", "nop", "} finally {", "ldstr \"protected EOF ran\"",
            "call void [System.Console]System.Console::WriteLine(string)", "}",
        ], TestContext.CancellationToken);

        Assert.AreEqual(0, code, output.ToString());
        Assert.Contains("protected EOF ran", output.ToString());
        Assert.IsTrue(engine.Status.CellIsEmpty, "EOF must execute the completed protected block.");
        var captured = await engine.SessionAsync(new SessionRequest(), TestContext.CancellationToken);
        Assert.AreEqual("succeeded", Assert.ContainsSingle(captured.Document.Cells).State);
    }

    /// <summary>
    /// Opening through the command resets EOF execution provenance from any earlier batch instructions.
    /// </summary>
    [TestMethod]
    public async Task OpenCommand_ClearsEarlierBatchExecutionProvenance()
    {
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(null, TestContext.CancellationToken);
        await using var engine = await SessionWorkspaceFixture.StartAsync(TestContext.CancellationToken);
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);

        var code = await runner.RunAsync(["ldc.i4.1", $".session open \"{files.SessionPath}\" --force", ".show"],
            TestContext.CancellationToken);

        Assert.AreEqual(0, code, output.ToString());
        Assert.Contains("Session opened. Nothing has run yet. Saved output is shown for reference.", output.ToString());
        Assert.IsFalse(File.Exists(files.MarkerPath), output.ToString());
        Assert.IsFalse(engine.Status.CellIsEmpty);
    }

    /// <summary>
    /// Batch replacement refuses dirty source without prompting and accepts an explicit force request.
    /// </summary>
    [TestMethod]
    public async Task OpenCommand_DirtySourceRequiresForce()
    {
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(null, TestContext.CancellationToken);
        await using var engine = await SessionWorkspaceFixture.StartAsync(TestContext.CancellationToken);
        Assert.IsTrue((await engine.HandleAsync("ldc.i4.7", TestContext.CancellationToken)).Succeeded);

        var refused = await engine.HandleAsync($".session open \"{files.SessionPath}\"", TestContext.CancellationToken);
        var retained = await engine.SessionAsync(new SessionRequest(), TestContext.CancellationToken);
        var opened = await engine.HandleAsync($".session open \"{files.SessionPath}\" --force", TestContext.CancellationToken);

        Assert.IsFalse(refused.Succeeded);
        Assert.Contains("unsaved source", string.Join('\n', refused.Lines.Select(line => line.PlainText)));
        Assert.AreEqual("ldc.i4.7", Assert.ContainsSingle(retained.Document.Entries).Source.Single());
        Assert.IsTrue(opened.Succeeded, string.Join('\n', opened.Lines.Select(line => line.PlainText)));
        Assert.IsNotNull(opened.SessionEditor);
        Assert.IsFalse(File.Exists(files.MarkerPath));
    }

    private async Task<SessionController> StartAsync(bool remote) => remote
        ? await SessionWorkspaceFixture.StartAsync(TestContext.CancellationToken)
        : new SessionController(new InProcessEngine(), _ => Task.FromResult<IReplEngine>(new InProcessEngine()));

    private async Task HydrateAsync(SessionController engine, SessionDocument document) =>
        await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Hydrate },
            Document = document,
        }, TestContext.CancellationToken);
}
