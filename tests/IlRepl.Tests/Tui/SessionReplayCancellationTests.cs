using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Verifies that interrupting replay terminates its host while preserving the complete saved experiment.
/// </summary>
[TestClass]
public sealed class SessionReplayCancellationTests
{
    /// <summary>
    /// Cancels test-owned terminal and host operations when the test is interrupted.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Quitting an active ordinary cell or replay closes its host without waiting for a dirty-source capture.
    /// </summary>
    /// <param name="replay">Whether execution comes from explicit replay instead of a newly entered cell.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Quit_RunningCellStopsWithoutDirtyPrompt(bool replay)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var document = replay ? Document(files.MarkerPath, Path.Combine(files.DirectoryPath, "later.txt")) : new SessionDocument();
        await files.WriteAsync(document, token);
        var original = await File.ReadAllBytesAsync(files.SessionPath, token);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, new Transcript(), onPrompt: value => prompt = value);
        var run = IlReplApp.RunAsync(terminal, prompt, token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync(replay ? "il[3]>" : "il[1]>");
        if (replay)
        {
            await AppTest.TypeLinesAsync(auto, [".session run"], token);
        }
        else
        {
            string[] source = ["ldstr \"" + files.MarkerPath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"",
                "ldstr \"started\"", "call void [System.IO.FileSystem]System.IO.File::WriteAllText(string, string)",
                "LOOP: br LOOP", "ret"];
            await AppTest.TypeLinesAsync(auto, source, token);
        }

        await auto.WaitUntilAsync(_ => File.Exists(files.MarkerPath) && new FileInfo(files.MarkerPath).Length == 7);
        Assert.IsTrue(prompt!.Submission is { IsRunning: true });
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(AppTest.Timeout, token);

        Assert.IsNull(prompt.SessionDialog, "Active execution must not queue a dirty check behind the running cell.");
        Assert.IsFalse(prompt.Submission?.IsRunning ?? false);
        Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(files.SessionPath, token));
        await engine.DisposeAsync();
    }

    /// <summary>
    /// Cancelling an infinite first cell preserves both cells and leaves a responsive inactive replacement host.
    /// </summary>
    /// <param name="shortcut">Whether cancellation uses the real terminal Ctrl+C shortcut.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Run_CancellationPreservesEntireExperiment(bool shortcut)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var secondMarker = Path.Combine(files.DirectoryPath, "second-cell.txt");
        var source = Document(files.MarkerPath, secondMarker);
        await files.WriteAsync(source, token);
        var original = await File.ReadAllBytesAsync(files.SessionPath, token);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        var epoch = engine.AssemblyVersion >> 32;
        PromptState? prompt = null;
        var transcript = new Transcript();
        await using var terminal = AppTest.Build(engine, transcript, onPrompt: value => prompt = value);
        var terminalRun = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[3]>");
        Task<SessionReply>? replay = null;
        if (shortcut)
        {
            await AppTest.TypeLinesAsync(auto, [".session run"], token);
        }
        else
        {
            replay = engine.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Run },
            }, token);
        }

        await auto.WaitUntilAsync(_ => File.Exists(files.MarkerPath) && new FileInfo(files.MarkerPath).Length == 7);
        Assert.IsFalse(File.Exists(secondMarker), "The infinite first cell must prevent the second cell from starting.");
        if (shortcut)
        {
            await auto.Ctrl().KeyAsync(Hex1bKey.C, ct: token);
            await auto.WaitUntilTextAsync("session run cancelled; all source remains available");
            await auto.WaitUntilAsync(_ => prompt is { Busy: false });
        }
        else
        {
            engine.CancelExecution();
            var result = await replay!.WaitAsync(AppTest.Timeout, token);
            Assert.IsFalse(result.Reply.Succeeded);
            Assert.Contains(line => line.PlainText.Contains("session run cancelled", StringComparison.Ordinal), result.Reply.Lines);
        }

        var captured = await engine.SessionAsync(new SessionRequest(), token);
        Assert.AreEqual(epoch + 1, engine.AssemblyVersion >> 32);
        Assert.AreEqual(files.SessionPath, captured.Path);
        Assert.IsTrue(captured.Dirty);
        Assert.AreSequenceEqual(source.Entries.Select(entry => entry.Identity), captured.Document.Entries.Select(entry => entry.Identity));
        Assert.AreSequenceEqual(source.Entries.SelectMany(entry => entry.Source),
            captured.Document.Entries.SelectMany(entry => entry.Source));
        Assert.AreSequenceEqual(source.Cells.Select(cell => cell.Identity), captured.Document.Cells.Select(cell => cell.Identity));
        Assert.AreSequenceEqual(["interrupted", "unrun"], captured.Document.Cells.Select(cell => cell.State));
        Assert.AreSequenceEqual(source.Cells[0].Source, captured.Document.Cells[0].Source);
        Assert.AreEqual(1, Assert.ContainsSingle(captured.Document.Interruptions).Number);
        Assert.AreSequenceEqual([".session run"], captured.Document.Interruptions[0].Source);
        Assert.AreEqual("started", await File.ReadAllTextAsync(files.MarkerPath, token));
        Assert.IsFalse(File.Exists(secondMarker));
        Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(files.SessionPath, token));

        var inspected = await engine.HandleAsync(".session cells", token);
        Assert.IsTrue(inspected.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("historical", StringComparison.Ordinal), inspected.Lines);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await auto.WaitUntilTextAsync("Save changes to ");
        await auto.TabAsync(ct: token);
        await auto.EnterAsync(ct: token);
        await terminalRun.WaitAsync(AppTest.Timeout, token);
    }

    private static SessionDocument Document(string firstMarker, string secondMarker)
    {
        string[] Marker(string path) =>
        [
            "ldstr \"" + path.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"", "ldstr \"started\"",
            "call void [System.IO.FileSystem]System.IO.File::WriteAllText(string, string)",
        ];
        string[] first = [.. Marker(firstMarker), "LOOP: br LOOP"];
        var second = Marker(secondMarker);
        return new SessionDocument
        {
            Entries = [new SessionEntry { Source = first }, new SessionEntry { Kind = SessionEntryKind.Run, Source = ["ret"] },
                new SessionEntry { Number = 2, Source = second },
                new SessionEntry { Number = 2, Kind = SessionEntryKind.Run, Source = ["ret"] }],
            Cells = [new SessionCell { Source = [.. first, "ret"] },
                new SessionCell { Number = 2, Source = [.. second, "ret"] }],
        };
    }
}
