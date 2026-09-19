using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Drives native inspection through the terminal's real submission and host worker path.
/// </summary>
[TestClass]
public sealed class NativeTerminalTests
{
    /// <summary>
    /// Supplies cancellation to the terminal and its actual CoreCLR host.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Native inspection completes its ticket in the terminal and leaves a file-writing cell pending until explicit return.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Submit_NativeInspectionPreservesPendingCellUntilExplicitRun()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var engine = await HostPaths.StartEngineAsync(token);
        foreach (var line in files.PendingDocument().Entries.SelectMany(entry => entry.Source))
        {
            Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded, line);
        }

        var before = engine.Status;
        var transcript = new Transcript();
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, transcript, onPrompt: value => prompt = value);
        var running = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");

        await auto.TypeAsync(".jit", ct: token);
        await auto.EnterAsync(ct: token);
        await auto.WaitUntilAsync(_ => prompt is { Busy: false }
            && transcript.Lines.Any(line => line.PlainText.Contains("native inspection: complete", StringComparison.Ordinal)));

        Assert.IsFalse(File.Exists(files.MarkerPath));
        Assert.AreEqual(before.CellNumber, engine.Status.CellNumber);
        Assert.AreEqual(before.Instructions, engine.Status.Instructions);
        Assert.IsFalse(engine.Status.CellIsEmpty);
        Assert.Contains(line => line.Kind == LineKind.Listing && line.Spans.Any(span => span.Style == SpanStyle.Opcode), transcript.Lines);
        Assert.DoesNotContain(line => line.Kind == LineKind.Result, transcript.Lines);
        await auto.TypeAsync("ret", ct: token);
        await auto.EnterAsync(ct: token);
        await auto.WaitUntilTextAsync("= 42 : int32");
        Assert.AreEqual("executed", await File.ReadAllTextAsync(files.MarkerPath, token));
        Assert.IsTrue(engine.Status.CellIsEmpty);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await running.WaitAsync(AppTest.Timeout, token);
    }
}
