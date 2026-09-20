using IlRepl.Batch;
using IlRepl.Engine;
using IlRepl.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Batch;

/// <summary>
/// Verifies that batch source exports do not gain an implicit execution at the end of input.
/// </summary>
[TestClass]
public sealed class SaveBatchTests
{
    /// <summary>
    /// Supplies cancellation for actual host execution and file writes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Assembly and session saves retain pending side-effecting IL without executing it when batch input ends.
    /// </summary>
    /// <param name="session">Whether the artifact is an editable session or exported assembly.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Save_AtEofDoesNotRunPendingSource(bool session)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        using var output = new StringWriter();
        var destination = Path.Join(files.DirectoryPath, session ? "retained.ilrepl.json" : "retained.dll");
        var runner = new BatchRunner(engine, output, color: false, echoInput: false);
        var code = await runner.RunAsync([
            "ldstr " + LiteralParser.Escape(files.MarkerPath), "ldstr \"unexpected execution\"",
            "call void System.IO.File::WriteAllText(string, string)",
            (session ? ".session save " : ".save ") + LiteralParser.Escape(destination),
        ], token);
        Assert.AreEqual(0, code, output.ToString());
        Assert.IsFalse(File.Exists(files.MarkerPath), "Saving cannot authorize pending user code at EOF.");
        Assert.IsFalse(engine.Status.CellIsEmpty);
        if (session)
        {
            var document = SessionCodec.Read(await File.ReadAllBytesAsync(destination, token));
            Assert.Contains("call void System.IO.File::WriteAllText(string, string)",
                document.Entries.SelectMany(entry => entry.Source));
            Assert.IsEmpty(document.Cells);
        }
        else
        {
            using var module = ModuleDefinition.ReadModule(destination);
            var body = module.GetType("IlRepl.Cell").Methods.Single(method => method.Name == "Run").Body;
            Assert.Contains(instruction => instruction.OpCode == OpCodes.Ldstr
                && Equals(instruction.Operand, files.MarkerPath), body.Instructions);
            Assert.Contains(instruction => instruction.OpCode == OpCodes.Call
                && instruction.Operand is MethodReference { Name: "WriteAllText", DeclaringType.FullName: "System.IO.File" },
                body.Instructions);
        }
    }
}
