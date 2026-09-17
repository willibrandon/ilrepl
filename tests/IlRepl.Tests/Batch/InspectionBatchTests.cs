using IlRepl.Batch;
using IlRepl.Repl;

namespace IlRepl.Tests.Batch;

/// <summary>
/// Verifies that inspection commands preserve pending batch code until execution is requested again.
/// </summary>
[TestClass]
public sealed class InspectionBatchTests
{
    /// <summary>
    /// Supplies cancellation to real engine requests and file operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Inspecting newly submitted code prevents implicit EOF execution, including aliases and failed inspections.
    /// </summary>
    /// <param name="command">The final inspection command.</param>
    /// <param name="expectedExit">Whether the inspection is expected to fail.</param>
    [TestMethod]
    [DataRow(".show", 0)]
    [DataRow(".list", 0)]
    [DataRow(".ls", 0)]
    [DataRow(".il", 0)]
    [DataRow(".dis Value", 0)]
    [DataRow(".dis\tValue", 0)]
    [DataRow(".disassemble Value", 0)]
    [DataRow(".diff Copy", 0)]
    [DataRow(".dis Missing", 1)]
    [DataRow(".diff Missing", 1)]
    [DataRow(".jit Missing", 1)]
    [DataRow(".jit\tMissing", 1)]
    public async Task Inspection_SuppressesPendingCellAtEof(string command, int expectedExit)
    {
        using var files = new SessionWorkspaceFixture();
        await using var engine = new InProcessEngine();
        await DefineEditableMethodAsync(engine);
        var cellNumber = engine.Status.CellNumber;
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);

        var code = await runner.RunAsync([
            .. files.PendingDocument().Entries.SelectMany(entry => entry.Source), command,
        ], TestContext.CancellationToken);

        Assert.AreEqual(expectedExit, code, output.ToString());
        Assert.IsFalse(File.Exists(files.MarkerPath), "Inspection must not run the pending file-writing body.");
        Assert.IsFalse(engine.Status.CellIsEmpty);
        Assert.AreEqual(4, engine.Status.Instructions);
        Assert.AreEqual(cellNumber, engine.Status.CellNumber);
        Assert.DoesNotContain("= 42 : int32", output.ToString());
    }

    /// <summary>
    /// Only accepted executable instructions restore EOF execution after an inspection command.
    /// </summary>
    /// <param name="command">The inspection command, which may fail.</param>
    /// <param name="expectedExit">The batch outcome before restoring execution intent.</param>
    [TestMethod]
    [DataRow(".show", 0)]
    [DataRow(".il", 0)]
    [DataRow(".dis Value", 0)]
    [DataRow(".dis\tValue", 0)]
    [DataRow(".diff Copy", 0)]
    [DataRow(".jit Missing", 1)]
    [DataRow(".jit\tMissing", 1)]
    public async Task Inspection_FollowedByNewInstructionRunsAtEof(string command, int expectedExit)
    {
        using var files = new SessionWorkspaceFixture();
        await using var engine = new InProcessEngine();
        await DefineEditableMethodAsync(engine);
        var cellNumber = engine.Status.CellNumber;
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);

        var code = await runner.RunAsync([
            .. files.PendingDocument().Entries.SelectMany(entry => entry.Source), command, "nop",
        ], TestContext.CancellationToken);

        Assert.AreEqual(expectedExit, code, output.ToString());
        Assert.AreEqual("executed", await File.ReadAllTextAsync(files.MarkerPath, TestContext.CancellationToken));
        Assert.IsTrue(engine.Status.CellIsEmpty);
        Assert.AreEqual(cellNumber + 1, engine.Status.CellNumber);
        Assert.Contains("= 42 : int32", output.ToString());
    }

    /// <summary>
    /// Explicit run commands, blank-line execution, and returns still execute an inspected cell immediately.
    /// </summary>
    /// <param name="execution">The explicit cell execution input.</param>
    [TestMethod]
    [DataRow(".run")]
    [DataRow("")]
    [DataRow("ret")]
    public async Task Inspection_FollowedByExplicitExecutionRunsCell(string execution)
    {
        using var files = new SessionWorkspaceFixture();
        await using var engine = new InProcessEngine();
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);

        var code = await runner.RunAsync([
            .. files.PendingDocument().Entries.SelectMany(entry => entry.Source), ".show", execution,
        ], TestContext.CancellationToken);

        Assert.AreEqual(0, code, output.ToString());
        Assert.AreEqual("executed", await File.ReadAllTextAsync(files.MarkerPath, TestContext.CancellationToken));
        Assert.IsTrue(engine.Status.CellIsEmpty);
        Assert.AreEqual(2, engine.Status.CellNumber);
        Assert.Contains("= 42 : int32", output.ToString());
    }

    /// <summary>
    /// Comments, non-executable declarations, administrative commands, and rejected instructions preserve inspection intent.
    /// </summary>
    /// <param name="following">The physical input following inspection.</param>
    /// <param name="expectedExit">Whether that input fails.</param>
    [TestMethod]
    [DataRow("// inspection remains final", 0)]
    [DataRow("/* comment\n.show\n*/", 0)]
    [DataRow(".help", 0)]
    [DataRow(".method int32 Other() {\nldc.i4.7\nret\n}", 0)]
    [DataRow("not.an.opcode", 1)]
    public async Task Inspection_FollowedByNonExecutableInputDoesNotRunAtEof(string following, int expectedExit)
    {
        using var files = new SessionWorkspaceFixture();
        await using var engine = new InProcessEngine();
        using var output = new StringWriter();
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);

        var code = await runner.RunAsync([
            .. files.PendingDocument().Entries.SelectMany(entry => entry.Source), "  .show // inspect",
            .. following.Split('\n'),
        ], TestContext.CancellationToken);

        Assert.AreEqual(expectedExit, code, output.ToString());
        Assert.IsFalse(File.Exists(files.MarkerPath));
        Assert.IsFalse(engine.Status.CellIsEmpty);
        Assert.AreEqual(4, engine.Status.Instructions);
        Assert.DoesNotContain("= 42 : int32", output.ToString());
    }

    private async Task DefineEditableMethodAsync(InProcessEngine engine)
    {
        string[] lines =
        [
            ".method int32 Value() {", "ldc.i4.1", "ret", "}",
            ".edit Value as Copy {", ".method public static int32 Value() cil managed {",
            "ldc.i4.1", "ret", "}", "}",
        ];
        foreach (var line in lines)
        {
            var reply = await engine.HandleAsync(line, TestContext.CancellationToken);
            Assert.IsTrue(reply.Succeeded, line + "\n" + string.Join('\n', reply.Lines.Select(item => item.PlainText)));
        }
    }
}
