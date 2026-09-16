using Hex1b;
using Hex1b.Automation;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Verifies that terminal assertions can distinguish completed frames from partially received synchronized output.
/// </summary>
[TestClass]
public sealed class FrameRecorderTests
{
    /// <summary>
    /// Supplies cancellation for the real terminal pumps.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A raw snapshot can contain mixed rows while the recorder retains the last complete synchronized frame.
    /// </summary>
    [TestMethod]
    public async Task FragmentedUpdate_PublishesOnlyAfterTheFrameEnds()
    {
        var ct = TestContext.CancellationToken;
        await using var workload = new Hex1bAppWorkloadAdapter();
        var recorder = new FrameRecorder();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 4)
            .AddPresentationFilter(recorder).Build();
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        try
        {
            workload.Write("\u001b[?2026h\u001b[Hcall old\u001b[2;1Hmembers 1/1\u001b[?2026l");
            await auto.WaitUntilAsync(_ => recorder.Count == 1, description: "the initial synchronized frame completes");

            workload.Write("\u001b[?2026h\u001b[Hcall new");
            await auto.WaitUntilAsync(snapshot => snapshot.GetLine(0).TrimEnd() == "call new"
                && snapshot.ContainsText("members 1/1"), description: "a raw snapshot exposes the partially updated rows");
            var complete = Assert.ContainsSingle(recorder.Frames);
            Assert.AreEqual("call old", complete.Lines[0].TrimEnd());
            Assert.AreEqual("members 1/1", complete.Lines[1].TrimEnd());

            workload.Write("\u001b[2;1H\u001b[2K\u001b[?2026l");
            await auto.WaitUntilAsync(_ => recorder.Count == 2, description: "the finished update becomes observable");
            complete = recorder.Frames[1];
            Assert.AreEqual("call new", complete.Lines[0].TrimEnd());
            Assert.AreEqual("", complete.Lines[1].TrimEnd());
            Assert.IsFalse(complete.Contains("members"));
        }
        finally
        {
            await workload.DisposeAsync();
            await run;
        }
    }
}
