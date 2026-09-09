using System.Text;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Exercises terminal bytes grouped into one input packet without bypassing the terminal's key decoder.
/// </summary>
[TestClass]
public sealed class PromptInputTests
{
    /// <summary>
    /// Supplies cancellation for terminal work.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Control keys mixed with printable or Unicode text edit the document and the resulting instruction executes.
    /// </summary>
    /// <param name="packet">The terminal input bytes before UTF-8 encoding.</param>
    /// <param name="expected">The resulting source line.</param>
    /// <param name="result">The executed value.</param>
    [TestMethod]
    [DataRow("ldc.i4.9\u007f7", "ldc.i4.7", "= 7 : int32")]
    [DataRow("ldstr \"界ab\u007f\u007f好\"", "ldstr \"界好\"", "= \"界好\" : string")]
    [DataRow("discard\u0015ldc.i4.7", "ldc.i4.7", "= 7 : int32")]
    [DataRow("discard\u0003ldc.i4.7", "ldc.i4.7", "= 7 : int32")]
    public async Task GroupedControlKeys_EditBeforeTheFollowingText(string packet, string expected, string result)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var transcript = new Transcript();
        var adapter = new ScriptedPresentationAdapter(100, 30);
        PromptState prompt = null!;
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript,
            onPrompt: value => prompt = value).WithPresentation(adapter).Build();
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await adapter.SendAsync(Encoding.UTF8.GetBytes(packet));
        await auto.WaitUntilAsync(_ => prompt.Text == expected, description: "each control key edits in packet order");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(_ => AppTest.Echoes(transcript).Any(line => line.EndsWith(expected, StringComparison.Ordinal)),
            description: "the edited instruction binds");
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync(result);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }
}
