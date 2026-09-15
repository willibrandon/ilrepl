using Hex1b.Input;
using IlRepl.Protocol;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Real terminal keystrokes open editable source, commit a revision, compare through RPC, and keep the original callable.
/// </summary>
[TestClass]
public sealed class MethodEditingTerminalTests
{
    /// <summary>
    /// The cancellation context for terminal and process operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The desktop editor receives the full document and commits an actual keyboard change through the normal submission flow.
    /// </summary>
    /// <returns>The completed terminal and RPC assertions.</returns>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Edit_KeyboardChangeCommitsAndCompares()
    {
        var token = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(token);
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript, width: 120, height: 40);
        var run = terminal.RunAsync(token);
        var auto = AppTest.Automate(terminal);
        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, [".method int32 Answer() {", "ldc.i4.s 42", "ret", "}"], token);
        await auto.WaitUntilTextAsync("end of method Answer");
        await AppTest.TypeLinesAsync(auto, [".edit Answer as Copy"], token);
        await auto.WaitUntilTextAsync("Enter sends 8 lines");
        await auto.Ctrl().KeyAsync(Hex1bKey.Home, ct: token);
        for (var index = 0; index < 4; index++)
        {
            await auto.KeyAsync(Hex1bKey.DownArrow, ct: token);
        }

        await auto.KeyAsync(Hex1bKey.End, ct: token);
        await auto.KeyAsync(Hex1bKey.Backspace, ct: token);
        await auto.TypeAsync("3", ct: token);
        await auto.EnterAsync(ct: token);
        await auto.WaitUntilTextAsync("edit Copy committed as revision 1");
        await AppTest.TypeLinesAsync(auto, [".diff Copy"], token);
        await auto.WaitUntilTextAsync("+ ldc.i4.s 43");
        await AppTest.TypeLinesAsync(auto, [".compare Copy ()"], token);
        await auto.WaitUntilTextAsync("Copy: different");
        Assert.Contains(line => line.PlainText.Contains("original: completed", StringComparison.Ordinal), transcript.Lines);
        Assert.Contains(line => line.PlainText.Contains("edited: completed", StringComparison.Ordinal), transcript.Lines);
        await AppTest.TypeLinesAsync(auto, ["call Copy", "ret"], token);
        await auto.WaitUntilTextAsync("= 43 : int32");
        await AppTest.TypeLinesAsync(auto, ["call Answer", "ret"], token);
        await auto.WaitUntilTextAsync("= 42 : int32");
        Assert.DoesNotContain(line => line.Kind == LineKind.Error, transcript.Lines);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run;
    }
}
