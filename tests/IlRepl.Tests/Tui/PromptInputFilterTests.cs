using System.Text;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Verifies host input callbacks observe the same ordered editor updates as normal terminal commands.
/// </summary>
[TestClass]
public sealed class PromptInputFilterTests
{
    /// <summary>
    /// Supplies cooperative cancellation to the real terminal loop.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A consumed Enter observes all preceding text or bracketed paste without submitting it or consuming subsequent keys.
    /// </summary>
    /// <param name="paste">Whether the source is delivered in a bracketed paste.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task InputFilter_ObservesKeysAndPastesInOrder(bool paste)
    {
        var token = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        PromptState? prompt = null;
        var captured = new TaskCompletionSource<SessionEditor>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript, onPrompt: state =>
        {
            prompt = state;
            state.FilterInput = input =>
            {
                if (input is not Hex1bKeyEvent { Key: Hex1bKey.Enter } || captured.Task.IsCompleted)
                {
                    return false;
                }

                captured.SetResult(state.CaptureSessionEditor());
                return true;
            };
        }).WithPresentation(adapter).Build();

        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        const string source = "ldc.i4.s 42";
        var input = paste ? "\x1b[200~" + source + "\x1b[201~\r " : source + "\r ";
        await adapter.SendAsync(Encoding.UTF8.GetBytes(input));
        var editor = await captured.Task.WaitAsync(AppTest.Timeout, token);
        Assert.AreSequenceEqual([source], editor.Lines);
        Assert.AreEqual(source.Length, editor.Caret);
        Assert.AreEqual(editor.Caret, editor.Anchor);
        await auto.WaitUntilAsync(_ => prompt?.Text == source + " ");
        Assert.IsEmpty(AppTest.Echoes(transcript));
        Assert.IsTrue(engine.Status.CellIsEmpty);
        await auto.EnterAsync(ct: token);
        await auto.WaitUntilTextAsync("stack [int32]");
        await AppTest.TypeLinesAsync(auto, ["ret"], token);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(AppTest.Timeout, token);
    }
}
