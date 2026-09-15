using System.Text;
using Hex1b;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tests.Engine;
using IlRepl.Tests.Shared;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Exception ranges accept the same whitespace in the editor and engine and submit through the real terminal.
/// </summary>
[TestClass]
public sealed class ExceptionRangeWhitespaceTests
{
    /// <summary>
    /// Supplies cancellation for terminal and host operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Space, tab, and mixed separators retain complete block balance and execute both exception paths.
    /// </summary>
    /// <param name="kind">The exception clause kind.</param>
    [TestMethod]
    [DataRow("catch")]
    [DataRow("filter")]
    [DataRow("finally")]
    [DataRow("fault")]
    public void Range_Whitespace_SubmitsAndExecutes(string kind)
    {
        foreach (var separator in new[] { " ", "\t", " \t " })
        {
            var source = ExceptionRangeExamples.Source(kind, separator);
            var scan = BlockBalance.Scan(source);
            Assert.AreEqual(0, scan.Depth);
            Assert.IsFalse(scan.AwaitingBrace);
            Assert.IsTrue(BlockBalance.IsComplete(source));
            var session = IlLines.Load(source.Split('\n'));
            var method = session.Methods.Single().Version.Body;
            Assert.AreEqual(kind == "finally" ? 42 : 40, method.Invoke(null, [0]));
            Assert.AreEqual(42, method.Invoke(null, [1]));
        }
    }

    /// <summary>
    /// A pasted tab-separated range submits on Enter and remains executable after committing a real edit over RPC.
    /// </summary>
    /// <param name="kind">The exception clause kind.</param>
    /// <returns>The completed terminal, edit, comparison, and execution assertions.</returns>
    [TestMethod]
    [DataRow("catch")]
    [DataRow("filter")]
    [DataRow("finally")]
    [DataRow("fault")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Range_TerminalPaste_CommitsAndCompares(string kind)
    {
        var token = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(token);
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(120, 40);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(token);
        var auto = AppTest.Automate(terminal);
        await auto.WaitUntilTextAsync("il[1]>");
        var source = ExceptionRangeExamples.Source(kind, "\t");
        await adapter.SendAsync(Encoding.UTF8.GetBytes("\x1b[200~" + source + "\x1b[201~\r"));
        await auto.WaitUntilTextAsync("end of method Work");
        var edit = ".edit Work as Copy {\n" + source + "\n}";
        await adapter.SendAsync(Encoding.UTF8.GetBytes("\x1b[200~" + edit + "\x1b[201~\r"));
        await auto.WaitUntilTextAsync("edit Copy committed as revision 1");
        await AppTest.TypeLinesAsync(auto, [".compare Copy (1)"], token);
        await auto.WaitUntilTextAsync("Copy: match");
        await AppTest.TypeLinesAsync(auto, ["ldc.i4.1", "call Copy", "ret"], token);
        await auto.WaitUntilTextAsync("= 42 : int32");
        Assert.DoesNotContain(line => line.Kind == LineKind.Error, transcript.Lines);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run;
    }
}
