using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Drives real session shortcuts and unsaved-source dialogs through Hex1b and replaceable desktop hosts.
/// </summary>
[TestClass]
public sealed class SessionTerminalTests
{
    /// <summary>
    /// Supplies cooperative cancellation to terminal and host operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Startup and the Open shortcut display saved history once while preserving the selected draft without execution.
    /// </summary>
    /// <param name="startup">Whether the session is opened before the terminal starts.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Open_DisplaysHistoryAndPreservesDraft(bool startup)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var editor = new SessionEditor { Lines = ["// restored draft"], Caret = 4, Anchor = 9 };
        var document = files.CompletedDocument(editor);
        await files.WriteAsync(document, token);
        File.Delete(files.MarkerPath);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        if (startup)
        {
            await engine.SessionAsync(new SessionRequest
            {
                Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
            }, token);
        }

        var transcript = new Transcript();
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, transcript, onPrompt: value => prompt = value);
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        if (!startup)
        {
            await auto.WaitUntilTextAsync("il[1]>");
            await auto.Ctrl().KeyAsync(Hex1bKey.O, ct: token);
            await auto.WaitUntilTextAsync("Open session");
            await EnterPathAsync(auto, files.SessionPath, token);
        }

        await auto.WaitUntilTextAsync("end of saved history; no code executed");
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.WaitUntilAsync(_ => prompt?.Text == "// restored draft" && !prompt.SessionBusy);

        Assert.AreSequenceEqual(document.Entries.SelectMany(entry => entry.Source.Select(line => "il[1]> " + line)),
            transcript.Lines.Where(line => line.Kind == LineKind.Input).Select(line => line.PlainText));
        Assert.HasCount(1, transcript.Lines.Where(line => line.Kind == LineKind.Result));
        Assert.AreEqual(4, prompt!.Editor.Cursor.Position.Value);
        Assert.AreEqual(9, prompt.Editor.Cursor.SelectionAnchor?.Value);
        Assert.IsFalse(File.Exists(files.MarkerPath));
        Assert.IsFalse(engine.Workspace!.Dirty);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(AppTest.Timeout, token);
    }

    /// <summary>
    /// The first save requests a path, Escape preserves selection, and the next save reuses its associated path.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SaveShortcut_FirstPathCancelAndReusePreserveDraft()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        var transcript = new Transcript();
        transcript.Add(LineKind.Info, "Retained transcript behind the dialog");
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, transcript, onPrompt: value => prompt = value);
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4.s 42", ct: token);
        await auto.Shift().KeyAsync(Hex1bKey.LeftArrow, ct: token);
        await auto.WaitUntilAsync(_ => prompt?.Editor.Cursor.SelectionAnchor is not null);
        var caret = prompt!.Editor.Cursor.Position.Value;
        var anchor = prompt.Editor.Cursor.SelectionAnchor!.Value.Value;

        await auto.Ctrl().KeyAsync(Hex1bKey.S, ct: token);
        await auto.WaitUntilTextAsync("Save session");
        var overlay = terminal.CreateSnapshot();
        Assert.IsTrue(overlay.ContainsText("Retained transcript behind the dialog"));
        Assert.IsTrue(overlay.ContainsText("ldc.i4.s 42"));
        await auto.TypeAsync("changed-path", ct: token);
        await auto.WaitUntilAsync(_ => prompt.SessionDialog?.Path.Contains("changed-path", StringComparison.Ordinal) == true);
        Assert.AreEqual("ldc.i4.s 42", prompt.Text, "Typing in the path dialog must not edit the source behind it.");
        await auto.KeyAsync(Hex1bKey.Escape, ct: token);
        await auto.WaitUntilAsync(_ => prompt.SessionDialog is null && !prompt.SessionBusy);
        await auto.WaitUntilNoTextAsync("Save session");

        Assert.AreEqual("ldc.i4.s 42", prompt.Text);
        Assert.AreEqual(caret, prompt.Editor.Cursor.Position.Value);
        Assert.AreEqual(anchor, prompt.Editor.Cursor.SelectionAnchor?.Value);
        Assert.IsFalse(File.Exists(files.SessionPath));

        await auto.Ctrl().KeyAsync(Hex1bKey.S, ct: token);
        await auto.WaitUntilTextAsync("Save session");
        await EnterPathAsync(auto, files.SessionPath, token);
        await auto.WaitUntilTextAsync("saved session ");
        await auto.WaitUntilAsync(_ => !prompt.SessionBusy);
        var saved = SessionCodec.Read(await File.ReadAllBytesAsync(files.SessionPath, token));
        Assert.AreEqual("ldc.i4.s 42", Assert.ContainsSingle(saved.Editor.Lines));
        Assert.AreEqual(caret, saved.Editor.Caret);
        Assert.AreEqual(anchor, saved.Editor.Anchor);
        Assert.IsEmpty(saved.Entries, "Saving a draft must not submit it to the engine.");

        await auto.KeyAsync(Hex1bKey.End, ct: token);
        await auto.TypeAsync(" // changed", ct: token);
        await auto.Ctrl().KeyAsync(Hex1bKey.S, ct: token);
        await auto.WaitUntilAsync(_ => !prompt.SessionBusy
            && transcript.Lines.Count(line => line.PlainText.StartsWith("  saved session ", StringComparison.Ordinal)) == 2);
        var resaved = SessionCodec.Read(await File.ReadAllBytesAsync(files.SessionPath, token));
        Assert.AreEqual("ldc.i4.s 42 // changed", Assert.ContainsSingle(resaved.Editor.Lines));
        Assert.IsNull(prompt.SessionDialog, "A subsequent save should reuse the associated filename.");
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(AppTest.Timeout, token);
    }

    /// <summary>
    /// Saving during a dialog frame clears the completed dialog without requiring another key or redraw request.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SaveShortcut_CompletionDuringDialogFrameReleasesEditor()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        var transcript = new Transcript();
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, transcript, onPrompt: value => prompt = value);
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4.s 42", ct: token);
        await auto.Ctrl().KeyAsync(Hex1bKey.S, ct: token);
        await auto.WaitUntilTextAsync("Save session");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PublishCheckpointAsync = (_, cancellationToken) => release.Task.WaitAsync(cancellationToken);
        var invalidate = prompt!.Invalidate!;
        prompt.Invalidate = () =>
        {
            invalidate();
            if (prompt.Events.Any(item => item.Kind == SubmissionEventKind.SessionDocument))
            {
                posted.TrySetResult();
            }
        };
        var armed = 1;
        var frame = 0;
        prompt.DocumentationTargetChanged = (_, _, _) =>
        {
            if (prompt.SessionDialog is not { Submitted: true } || Volatile.Read(ref armed) == 0)
            {
                return;
            }

            if (Interlocked.Increment(ref frame) == 1)
            {
                // Move past the Enter frame so its pending wake-up cannot conceal a lost completion redraw.
                invalidate();
                return;
            }

            Interlocked.Exchange(ref armed, 0);
            release.TrySetResult();
            posted.Task.WaitAsync(AppTest.Timeout, token).GetAwaiter().GetResult();
        };
        try
        {
            await EnterPathAsync(auto, files.SessionPath, token);
            await posted.Task.WaitAsync(AppTest.Timeout, token);
            await auto.WaitUntilAsync(_ => prompt.SessionDialog is null && !prompt.SessionBusy);
            Assert.AreEqual(0, Volatile.Read(ref armed), "The completion must arrive after the dialog frame has drained events.");
            Assert.Contains(line => line.PlainText.Contains("saved session ", StringComparison.Ordinal), transcript.Lines);
            Assert.AreEqual("ldc.i4.s 42", prompt.Text);
            var saved = SessionCodec.Read(await File.ReadAllBytesAsync(files.SessionPath, token));
            Assert.AreSequenceEqual(["ldc.i4.s 42"], saved.Editor.Lines);
            Assert.IsEmpty(saved.Entries);
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
            await run.WaitAsync(AppTest.Timeout, token);
        }
        finally
        {
            release.TrySetResult();
            prompt.Invalidate = invalidate;
            prompt.DocumentationTargetChanged = null;
        }
    }

    /// <summary>
    /// Opening a saved experiment restores its draft and selection while accepted instructions remain unexecuted.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task OpenShortcut_RestoresSourceAndSelectionWithoutExecuting()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var editor = new SessionEditor { Lines = ["// retained draft"], Caret = 3, Anchor = 8, Revision = 7 };
        await files.WriteAsync(files.PendingDocument(editor), token);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        var transcript = new Transcript();
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, transcript, onPrompt: value => prompt = value);
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");

        await auto.Ctrl().KeyAsync(Hex1bKey.O, ct: token);
        await auto.WaitUntilTextAsync("Open session");
        await EnterPathAsync(auto, files.SessionPath, token);
        await auto.WaitUntilTextAsync("Session opened. Nothing has run yet.");
        await auto.WaitUntilAsync(_ => prompt?.Text == "// retained draft" && !prompt.SessionBusy);

        Assert.AreEqual(3, prompt!.Editor.Cursor.Position.Value);
        Assert.AreEqual(8, prompt.Editor.Cursor.SelectionAnchor?.Value);
        Assert.IsFalse(engine.Status.CellIsEmpty);
        Assert.IsFalse(File.Exists(files.MarkerPath));
        Assert.DoesNotContain(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), transcript.Lines);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(AppTest.Timeout, token);
    }

    /// <summary>
    /// A dirty file prompts on quit, Escape cancels without losing text, and Discard remains keyboard accessible.
    /// </summary>
    /// <param name="typed">Whether quit is typed instead of invoked with the shortcut.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Quit_DirtyFileCanCancelThenDiscard(bool typed)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(new SessionDocument(), token);
        var original = await File.ReadAllBytesAsync(files.SessionPath, token);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, new Transcript(), onPrompt: value => prompt = value);
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["ldc.i4.7"], token);
        await auto.WaitUntilTextAsync("[int32]");

        await QuitAsync(auto, typed, token);
        await auto.WaitUntilTextAsync("Save changes to ");
        await auto.KeyAsync(Hex1bKey.Escape, ct: token);
        await auto.WaitUntilAsync(_ => prompt?.SessionDialog is null && !prompt!.Busy);

        Assert.IsFalse(run.IsCompleted, "Cancelling quit keeps the terminal running.");
        Assert.IsFalse(engine.Status.CellIsEmpty);
        Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(files.SessionPath, token));

        await QuitAsync(auto, typed, token);
        await auto.WaitUntilTextAsync("Save changes to ");
        await auto.TabAsync(ct: token);
        await auto.EnterAsync(ct: token);
        await run.WaitAsync(AppTest.Timeout, token);
        Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(files.SessionPath, token));
    }

    /// <summary>
    /// Saving from the quit dialog writes the unsent editor and completes the original quit action.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Quit_SaveWritesDraftAndExits()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(new SessionDocument(), token);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        await using var terminal = AppTest.Build(engine, new Transcript());
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync(".method int32 Unfinished() {", ct: token);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await auto.WaitUntilTextAsync("Save changes to ");
        await auto.EnterAsync(ct: token);
        await run.WaitAsync(AppTest.Timeout, token);

        var saved = SessionCodec.Read(await File.ReadAllBytesAsync(files.SessionPath, token));
        Assert.AreEqual(".method int32 Unfinished() {", Assert.ContainsSingle(saved.Editor.Lines));
        Assert.IsEmpty(saved.Entries);
    }

    /// <summary>
    /// An approved quit keeps its dialog visible until the final checkpoint completes instead of flashing the editor.
    /// </summary>
    /// <param name="typed">Whether quit is submitted as a command.</param>
    /// <param name="save">Whether the confirmation saves the changed source.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Quit_ConfirmationRemainsVisibleUntilExit(bool typed, bool save)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(new SessionDocument(), token);
        var original = await File.ReadAllBytesAsync(files.SessionPath, token);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        PromptState? prompt = null;
        var recorder = new FrameRecorder();
        await using var terminal = AppTest.Build(engine, new Transcript(), onPrompt: value => prompt = value,
            configure: builder => builder.AddPresentationFilter(recorder));
        recorder.Terminal = terminal;
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await AppTest.TypeLinesAsync(auto, ["ldc.i4.7"], token);
        await auto.WaitUntilTextAsync("[int32]");
        await QuitAsync(auto, typed, token);
        await auto.WaitUntilTextAsync("Save changes to ");
        var dialog = prompt!.SessionDialog;
        Assert.IsNotNull(dialog);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PublishCheckpointAsync = async (_, cancellationToken) =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var firstFrame = recorder.Count;
        try
        {
            if (!save)
            {
                await auto.KeyAsync(Hex1bKey.DownArrow, ct: token);
            }

            await auto.EnterAsync(ct: token);
            await reached.Task.WaitAsync(AppTest.Timeout, token);
            prompt.Invalidate!.Invoke();
            await auto.WaitUntilAsync(_ => recorder.Count > firstFrame);
            Assert.AreSame(dialog, prompt.SessionDialog);
            Assert.IsTrue(dialog.Submitted);
            Assert.IsFalse(run.IsCompleted);
            Assert.DoesNotContain(frame => !frame.Contains("Save changes to "), recorder.Since(firstFrame));
        }
        finally
        {
            release.TrySetResult();
        }

        await run.WaitAsync(AppTest.Timeout, token);
        Assert.DoesNotContain(frame => !frame.Contains("Save changes to "), recorder.Since(firstFrame));
        var bytes = await File.ReadAllBytesAsync(files.SessionPath, token);
        if (save)
        {
            Assert.Contains("ldc.i4.7", SessionCodec.Read(bytes).Entries.SelectMany(entry => entry.Source));
        }
        else
        {
            Assert.AreSequenceEqual(original, bytes);
        }
    }

    /// <summary>
    /// A filesystem failure while saving on quit dismisses the resolved dialog and leaves the source editable.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Quit_SaveFailureClosesDialogAndKeepsEditor()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(new SessionDocument(), token);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        File.Delete(files.SessionPath);
        Directory.CreateDirectory(files.SessionPath);
        Hex1bApp? app = null;
        PromptState? prompt = null;
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript,
            onApp: value => app = value, onPrompt: value => prompt = value).WithHeadless().WithDimensions(100, 30).Build();
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("// preserved draft", ct: token);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await auto.WaitUntilTextAsync("Save changes to ");
        var failedDialog = prompt!.SessionDialog;
        Assert.IsNotNull(failedDialog);
        await auto.EnterAsync(ct: token);
        await auto.WaitUntilAsync(screen => prompt.SessionDialog is null && !prompt.Busy && app?.FocusedNode is EditorNode
            && !screen.ContainsText("Save changes to ") && screen.ContainsText("// preserved draft")
            && transcript.Lines.Any(line => line.Kind == LineKind.Error));
        Assert.IsFalse(run.IsCompleted);
        Assert.AreEqual("// preserved draft", prompt!.Text);
        Assert.Contains(line => line.Kind == LineKind.Error && line.PlainText.Contains(files.SessionPath, StringComparison.Ordinal),
            transcript.Lines);
        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await auto.WaitUntilAsync(screen => prompt.SessionDialog is { Submitted: false } dialog
            && !ReferenceEquals(failedDialog, dialog) && app?.FocusedNode is ButtonNode { Label: "Save" }
            && screen.ContainsText("Save changes to "));
        Assert.AreNotSame(failedDialog, prompt.SessionDialog);
        await auto.KeyAsync(Hex1bKey.DownArrow, ct: token);
        await auto.WaitUntilAsync(_ => app?.FocusedNode is ButtonNode { Label: "Discard" });
        await auto.EnterAsync(ct: token);
        await run.WaitAsync(AppTest.Timeout, token);
    }

    /// <summary>
    /// Arrows and Tab share a wrapping focus order, and Enter applies the selected unsaved-source decision.
    /// </summary>
    /// <param name="save">Whether the final arrow-selected choice saves or discards the draft.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SaveChanges_ArrowsWrapAndActivateFocusedChoice(bool save)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(new SessionDocument(), token);
        var original = await File.ReadAllBytesAsync(files.SessionPath, token);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        Hex1bApp? app = null;
        PromptState? prompt = null;
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, new Transcript(),
            onApp: value => app = value, onPrompt: value => prompt = value).WithHeadless().WithDimensions(100, 30).Build();
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("// unsaved draft", ct: token);
        await auto.Shift().KeyAsync(Hex1bKey.LeftArrow, ct: token);
        await auto.WaitUntilAsync(_ => prompt?.Editor.Cursor.SelectionAnchor is not null);
        var caret = prompt!.Editor.Cursor.Position.Value;
        var anchor = prompt.Editor.Cursor.SelectionAnchor!.Value.Value;

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await auto.WaitUntilAsync(_ => app?.FocusedNode is ButtonNode { Label: "Save" });
        await MoveAsync(Hex1bKey.DownArrow, "Discard");
        await MoveAsync(Hex1bKey.DownArrow, "Cancel");
        await MoveAsync(Hex1bKey.DownArrow, "Save");
        await MoveAsync(Hex1bKey.UpArrow, "Cancel");
        await MoveAsync(Hex1bKey.UpArrow, "Discard");
        await MoveAsync(Hex1bKey.Tab, "Cancel");
        await auto.Shift().KeyAsync(Hex1bKey.Tab, ct: token);
        await auto.WaitUntilAsync(_ => app?.FocusedNode is ButtonNode { Label: "Discard" });
        await MoveAsync(Hex1bKey.UpArrow, "Save");
        await MoveAsync(Hex1bKey.UpArrow, "Cancel");
        await auto.EnterAsync(ct: token);
        await auto.WaitUntilAsync(_ => prompt.SessionDialog is null && !prompt.SessionBusy);

        Assert.IsFalse(run.IsCompleted, "Selecting Cancel must leave the session running.");
        Assert.AreEqual("// unsaved draft", prompt.Text);
        Assert.AreEqual(caret, prompt.Editor.Cursor.Position.Value);
        Assert.AreEqual(anchor, prompt.Editor.Cursor.SelectionAnchor?.Value);
        Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(files.SessionPath, token));

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await auto.WaitUntilAsync(_ => app?.FocusedNode is ButtonNode { Label: "Save" });
        await MoveAsync(Hex1bKey.DownArrow, "Discard");
        if (save)
        {
            await MoveAsync(Hex1bKey.UpArrow, "Save");
        }

        await auto.EnterAsync(ct: token);
        await run.WaitAsync(AppTest.Timeout, token);

        var bytes = await File.ReadAllBytesAsync(files.SessionPath, token);
        if (save)
        {
            var saved = SessionCodec.Read(bytes);
            Assert.AreEqual("// unsaved draft", Assert.ContainsSingle(saved.Editor.Lines));
            Assert.AreEqual(caret, saved.Editor.Caret);
            Assert.AreEqual(anchor, saved.Editor.Anchor);
            Assert.IsEmpty(saved.Entries);
        }
        else
        {
            Assert.AreSequenceEqual(original, bytes, "Selecting Discard must leave the saved file unchanged.");
        }

        async Task MoveAsync(Hex1bKey key, string label)
        {
            await auto.KeyAsync(key, ct: token);
            await auto.WaitUntilAsync(_ => app?.FocusedNode is ButtonNode button && button.Label == label,
                description: key + " focuses " + label);
        }
    }

    /// <summary>
    /// A scratch experiment that was never saved exits directly even with unsent source.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Quit_UnsavedScratchDoesNotPrompt()
    {
        var token = TestContext.CancellationToken;
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, new Transcript(), onPrompt: value => prompt = value);
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4.7", ct: token);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(AppTest.Timeout, token);

        Assert.IsNull(prompt!.SessionDialog);
        Assert.AreEqual("ldc.i4.7", prompt.Text);
        Assert.IsTrue(engine.Status.CellIsEmpty, "Quitting must not submit the scratch editor.");
    }

    /// <summary>
    /// An untouched empty document remains clean after its editor is rendered and exits without a save prompt.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Quit_CleanEmptyFileDoesNotPrompt()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await files.WriteAsync(new SessionDocument(), token);
        await using var engine = await SessionWorkspaceFixture.StartAsync(token);
        await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);
        PromptState? prompt = null;
        await using var terminal = AppTest.Build(engine, new Transcript(), onPrompt: value => prompt = value);
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: AppTest.Timeout);
        await auto.WaitUntilTextAsync("il[1]>");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        await run.WaitAsync(AppTest.Timeout, token);

        Assert.IsNull(prompt!.SessionDialog);
        var current = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
            Editor = engine.Editor,
        }, token);
        Assert.IsFalse(current.Dirty);
        Assert.AreEqual(files.SessionPath, current.Path);
        Assert.IsEmpty(current.Document.Entries);
    }

    private static async Task EnterPathAsync(Hex1bTerminalAutomator auto, string path, CancellationToken token)
    {
        await auto.Ctrl().KeyAsync(Hex1bKey.A, ct: token);
        await auto.TypeAsync(path, ct: token);
        await auto.EnterAsync(ct: token);
    }

    private static async Task QuitAsync(Hex1bTerminalAutomator auto, bool typed, CancellationToken token)
    {
        if (typed)
        {
            await AppTest.TypeLinesAsync(auto, [".quit"], token);
        }
        else
        {
            await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: token);
        }
    }
}
