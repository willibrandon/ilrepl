using Hex1b.Documents;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Exercises bounded request lifetimes with real candidate pages and independently gated response delivery.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class CompletionRequesterTests
{
    /// <summary>
    /// Supplies cancellation for real computations and condition-based waits.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Superseded delivery cannot reopen the palette and only the latest queued document reaches the engine.
    /// </summary>
    [TestMethod]
    public async Task Refresh_OutOfOrderReplies_DropsSupersededRows()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        var stale = await engine.Calls[0].Prepared;
        state.Editor.InsertText("i");
        requester.Refresh(state);
        state.Editor.InsertText("t");
        requester.Refresh(state);
        Assert.HasCount(1, engine.Calls);
        Assert.IsTrue(engine.Calls[0].Cancellation.IsCancellationRequested);
        engine.Calls[0].Release.SetResult();
        await WaitAsync(() =>
        {
            requester.Refresh(state);
            return engine.Calls.Count == 2;
        });
        Assert.AreEqual("call Console::Writ", engine.Calls[1].Request.Lines[0]);
        Assert.IsNull(state.Completions);
        await ReleaseAsync(engine, state, 1);
        Assert.AreEqual("call Console::Writ", state.Completions!.Key.Document.Lines[0]);
        Assert.AreNotEqual(stale.QueryId, state.Completions.Reply.QueryId);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
        Assert.IsNull(CompletionEdit.For(state, stale.Items[0]));
    }

    /// <summary>
    /// Previous real rows preserve layout while pending but cannot supply an edit or survive dismissal.
    /// </summary>
    [TestMethod]
    public async Task Refresh_ReplacementPending_KeepsOnlyDisabledDisplayRows()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        await ReleaseAsync(engine, state, 0);
        var original = state.Completions!.Reply.Items;
        state.Editor.InsertText("i");
        requester.Refresh(state);
        var display = PromptWidget.DisplayCandidates(state, engine.Catalog);
        Assert.HasCount(original.Count, display);
        Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
        Assert.IsNull(CompletionEdit.For(state, display[0]));
        Assert.AreEqual("call Console::Wri", state.Text);
        requester.Cancel(state);
        Assert.IsNull(state.PendingDisplay);
        Assert.IsEmpty(PromptWidget.DisplayCandidates(state, engine.Catalog));
        await CloseAsync(engine, requester);
    }

    /// <summary>
    /// The reply consumer accepts empty intermediate protocol pages while retaining only disabled earlier rows.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Refresh_EmptyReplacementPages_PreserveDisabledRows(bool hasMatch)
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call string::Substring");
        var requester = state.Requester!;
        requester.Refresh(state);
        await ReleaseAsync(engine, state, 0);
        var original = state.Completions!.Reply.Items;
        for (var index = 0; index < "Substring".Length; index++)
        {
            state.Editor.DeleteBackward();
        }

        requester.Request(state);
        for (var page = 1; page <= 2; page++)
        {
            var result = await ReleaseAsync(engine, state, page, apply: false);
            Assert.IsNotNull(result.Reply!.Cursor, "The real string member query must provide another page.");
            requester.Apply(state, result with { Reply = result.Reply with { Items = [] } });
            Assert.HasCount(original.Count, PromptWidget.DisplayCandidates(state, engine.Catalog));
            Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
            Assert.IsNull(CompletionEdit.For(state, original[0]));
            requester.Refresh(state);
            Assert.AreEqual(result.Reply.Cursor, engine.Calls[page + 1].Request.Cursor);
        }

        var final = await ReleaseAsync(engine, state, 3, apply: false);
        var items = hasMatch ? final.Reply!.Items.Take(1).ToArray() : [];
        requester.Apply(state, final with { Reply = final.Reply! with { Items = items, Cursor = null, Total = items.Length } });
        Assert.IsNull(state.PendingDisplay);
        Assert.HasCount(items.Length, PromptWidget.DisplayCandidates(state, engine.Catalog));
        Assert.HasCount(items.Length, PromptWidget.Candidates(state, engine.Catalog));
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Editing during an empty protocol page cancels its real continuation while preserving the disabled original rows.
    /// </summary>
    [TestMethod]
    public async Task Refresh_EditDuringEmptyPage_PreservesDisabledRows()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call string::Substring");
        var requester = state.Requester!;
        requester.Refresh(state);
        await ReleaseAsync(engine, state, 0);
        var originalCount = state.Completions!.Reply.Items.Count;
        for (var index = 0; index < "Substring".Length; index++)
        {
            state.Editor.DeleteBackward();
        }

        requester.Request(state);
        var result = await ReleaseAsync(engine, state, 1, apply: false);
        requester.Apply(state, result with { Reply = result.Reply! with { Items = [] } });
        requester.Refresh(state);
        Assert.HasCount(3, engine.Calls);
        state.Editor.InsertText("S");
        requester.Refresh(state);
        Assert.IsTrue(engine.Calls[2].Cancellation.IsCancellationRequested);
        Assert.HasCount(originalCount, PromptWidget.DisplayCandidates(state, engine.Catalog));
        Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
        await CloseAsync(engine, requester);
    }

    /// <summary>
    /// Redraws retain one active request and repeated explicit retries coalesce until cancellation settles.
    /// </summary>
    [TestMethod]
    public async Task Refresh_PendingAndDismissed_OnlyRequestsWhenNeeded()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        for (var index = 0; index < 20; index++)
        {
            requester.Refresh(state);
        }

        Assert.HasCount(1, engine.Calls);
        requester.Cancel(state);
        state.PaletteDismissed = true;
        requester.Refresh(state);
        Assert.HasCount(1, engine.Calls);
        requester.Request(state);
        requester.Request(state);
        Assert.HasCount(1, engine.Calls);
        engine.Calls[0].Release.SetResult();
        await WaitAsync(() =>
        {
            requester.Refresh(state);
            return engine.Calls.Count == 2;
        });
        Assert.AreEqual(PaletteMode.Requested, state.Palette);
        await ReleaseAsync(engine, state, 1);
        Assert.AreEqual(PaletteMode.Open, state.Palette);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Delivery outcome handling keeps faults dismissed until retry and leaves cancellation retryable.
    /// </summary>
    [TestMethod]
    public async Task Refresh_FaultAndCancellation_ApplyDifferentRetryRules()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        var result = await ReleaseAsync(engine, state, 0, apply: false);
        requester.Apply(state, result with { Reply = null, Faulted = true });
        requester.Refresh(state);
        Assert.AreEqual(PaletteMode.Faulted, state.Palette);
        Assert.HasCount(1, engine.Calls);
        requester.Request(state);
        engine.Calls[1].Release.SetCanceled(TestContext.CancellationToken);
        await DrainAsync(state);
        Assert.IsNull(requester.LastAnswered);
        requester.Refresh(state);
        Assert.HasCount(3, engine.Calls);
        await ReleaseAsync(engine, state, 2);
        Assert.AreEqual(PaletteMode.Open, state.Palette);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Genuine string-member pages retain the real cursor and query identity through repeated navigation and redraws.
    /// </summary>
    [TestMethod]
    public async Task Refresh_PagedResults_PreservesCursorAndQueryStamps()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call string::");
        var requester = state.Requester!;
        requester.Request(state);
        await ReleaseAsync(engine, state, 0);
        var first = state.Completions!.Reply;
        Assert.HasCount(CompletionReply.PageSize, first.Items);
        Assert.IsNotNull(first.Cursor);
        requester.RequestMore(state);
        requester.Refresh(state);
        Assert.AreEqual(first.Cursor, engine.Calls[1].Request.Cursor);
        requester.RequestMore(state);
        for (var index = 0; index < 20; index++)
        {
            requester.Refresh(state);
        }

        Assert.HasCount(2, engine.Calls);
        await ReleaseAsync(engine, state, 1);
        var combined = state.Completions!.Reply;
        Assert.IsGreaterThan(first.Items.Count, combined.Items.Count);
        Assert.AreEqual(first.QueryId, combined.QueryId);
        Assert.AreEqual(first.BindingEpoch, combined.BindingEpoch);
        Assert.HasCount(combined.Items.Count, combined.Items.Distinct().ToArray());
        Assert.IsFalse(state.MoreCompletions);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Real comment mutations invalidate rows even when the document and session counts stay the same.
    /// </summary>
    [TestMethod]
    public async Task Refresh_CommentMutation_RequeriesAndRefusesOldEdits()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        state.Requester!.Refresh(state);
        await ReleaseAsync(engine, state, 0);
        var old = state.Completions!.Reply.Items[0];
        Assert.IsNotNull(CompletionEdit.For(state, old));
        await engine.HandleAsync("/*", TestContext.CancellationToken);
        Assert.IsNull(CompletionEdit.For(state, old));
        await engine.HandleAsync("*/", TestContext.CancellationToken);
        state.Requester.Refresh(state);
        Assert.HasCount(2, engine.Calls);
        await ReleaseAsync(engine, state, 1);
        Assert.IsNotNull(CompletionEdit.For(state, state.Completions!.Reply.Items[0]));
        await state.Requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Empty type sites wait for Tab and the actual prepared result becomes usable as soon as it is applied.
    /// </summary>
    [TestMethod]
    public async Task Refresh_EmptyBroadSite_WaitsForExplicitRequest()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call ");
        state.Requester!.Refresh(state);
        Assert.IsEmpty(engine.Calls);
        state.Requester.Request(state);
        Assert.HasCount(1, engine.Calls);
        await ReleaseAsync(engine, state, 0);
        Assert.AreEqual(PaletteMode.Open, state.Palette);
        Assert.IsFalse(state.Requester.IsPending);
        Assert.IsNotEmpty(PromptWidget.Candidates(state, engine.Catalog));
        await state.Requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Moving the caret invalidates rows before a redraw and resets navigation without changing the source.
    /// </summary>
    [TestMethod]
    public async Task Refresh_CaretMove_InvalidatesRowsImmediately()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        state.Requester!.Refresh(state);
        await ReleaseAsync(engine, state, 0);
        state.SelectedIndex = 7;
        state.PaletteNavigated = true;
        state.DetailScroll = 9;
        state.Editor.SetCursorPosition(new DocumentOffset(14));
        Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
        state.Requester.Refresh(state);
        Assert.AreEqual(0, state.SelectedIndex);
        Assert.AreEqual(0, state.DetailScroll);
        Assert.IsFalse(state.PaletteNavigated);
        Assert.AreEqual("call Console::Wr", state.Text);
        await CloseAsync(engine, state.Requester);
    }

    /// <summary>
    /// A current bound preview remains usable while a submission is pending and remains current after it settles.
    /// </summary>
    [TestMethod]
    public async Task Refresh_BusySubmission_PreservesCurrentPreview()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Submission = new Submission(engine, [], 0, false, ct => release.Task.WaitAsync(ct), _ =>
        {
        });
        try
        {
            requester.Refresh(state);
            Assert.IsFalse(engine.Calls[0].Cancellation.IsCancellationRequested);
            Assert.IsFalse(state.Submission.Completion.IsCompleted);
            await ReleaseAsync(engine, state, 0);
            Assert.AreEqual(PaletteMode.Open, state.Palette);
            Assert.Contains(item => item.Name.StartsWith("Write", StringComparison.Ordinal),
                PromptWidget.Candidates(state, engine.Catalog));
            release.TrySetResult();
            await state.Submission.Completion;
            state.Submission = null;
            requester.Refresh(state);
            Assert.HasCount(1, engine.Calls);
            Assert.AreEqual(PaletteMode.Open, state.Palette);
            Assert.IsEmpty(state.Events);
        }
        finally
        {
            release.TrySetResult();
            if (state.Submission is { } pending)
            {
                await pending.Completion;
            }

            await requester.SettleAsync(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Shutdown settles obsolete delivery before a new requester can publish into its own independent prompt.
    /// </summary>
    [TestMethod]
    public async Task Settle_SupersededTasks_CannotPublishIntoTheNextSession()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        state.Requester!.Refresh(state);
        state.Editor.InsertText("i");
        state.Requester.Refresh(state);
        var settling = state.Requester.SettleAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(settling.IsCompleted);
        Assert.HasCount(1, engine.Calls);
        Assert.IsTrue(engine.Calls[0].Cancellation.IsCancellationRequested);
        engine.Calls[0].Release.SetResult();
        await settling;
        var replacement = State(engine, "call Console::WriteLine");
        replacement.Requester!.Refresh(replacement);
        await ReleaseAsync(engine, replacement, 1);
        Assert.AreEqual("call Console::WriteLine", replacement.Completions!.Key.Document.Lines[0]);
        Assert.IsNotEmpty(replacement.Completions.Reply.Items);
        Assert.IsEmpty(state.Events);
        Assert.IsEmpty(replacement.Events);
        await replacement.Requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// A genuine background assembly load wakes the prompt and invalidates rows without changing accepted source.
    /// </summary>
    [TestMethod]
    public async Task Refresh_AssemblyLoad_InvalidatesCachedRowsAndRequeries()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        await ReleaseAsync(engine, state, 0);
        var old = state.Completions!;
        state.PaletteNavigated = true;
        Assert.IsNotNull(CompletionEdit.For(state, old.Reply.Items[0]));
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Invalidate = () => invalidated.TrySetResult();
        CompletionEngine.ChangeAssemblies();
        Assert.IsFalse(requester.Matches(state, old));
        Assert.IsNull(CompletionEdit.For(state, old.Reply.Items[0]));
        Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
        Assert.AreSequenceEqual(old.Visible(), PromptWidget.DisplayCandidates(state, engine.Catalog),
            "Visible navigation remains available between the assembly event and the next frame's refresh.");
        Assert.AreEqual(EnterAction.AcceptCompletion,
            PromptWidget.EnterActionFor(state, paletteVisible: false, openDepth: 0, commentOpen: false));
        state.PaletteDismissed = true;
        Assert.AreEqual(EnterAction.Submit,
            PromptWidget.EnterActionFor(state, paletteVisible: false, openDepth: 0, commentOpen: false));
        state.PaletteDismissed = false;
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
        requester.Refresh(state);
        await ReleaseAsync(engine, state, 1);
        Assert.AreEqual(old.Key.Document, state.Completions!.Key.Document);
        Assert.AreEqual(old.Key.Revision, state.Completions.Key.Revision);
        Assert.IsGreaterThan(old.Reply.AssemblyVersion, state.Completions.Reply.AssemblyVersion);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// A real assembly load during held delivery makes that old snapshot ineligible for publication.
    /// </summary>
    [TestMethod]
    public async Task Apply_AssemblyLoad_DropsThePendingReply()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        await engine.Calls[0].Prepared;
        CompletionEngine.ChangeAssemblies();
        await ReleaseAsync(engine, state, 0);
        Assert.IsNull(state.Completions);
        Assert.IsNull(requester.LastAnswered);
        requester.Refresh(state);
        await ReleaseAsync(engine, state, 1);
        Assert.IsNotNull(state.Completions);
        Assert.AreEqual(engine.AssemblyVersion, state.Completions.Reply.AssemblyVersion);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// An explicit acceptance waits for a fresh real binding after a genuine background assembly load.
    /// </summary>
    [TestMethod]
    public async Task Acceptance_AssemblyChange_RebindsThenAppliesOneUndoableEdit()
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        const string original = "call Environment::get_CurrentManagedTh";
        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, original);
        var requester = state.Requester!;
        requester.Refresh(state);
        await ReleaseAsync(engine, state, 0);
        var selected = state.Completions!.Reply.Items.Single();
        CompletionEngine.ChangeAssemblies();
        Assert.IsFalse(CompletionEdit.Accept(state, selected));
        requester.QueueAcceptance(state, selected);
        Assert.AreEqual(original, state.Text);
        await WaitAsync(() =>
        {
            requester.Refresh(state);
            return engine.Calls.Count == 2;
        });
        await ReleaseAsync(engine, state, 1);
        Assert.AreEqual("call Environment::get_CurrentManagedThreadId()", state.Text);
        Assert.IsTrue(state.PaletteDismissed);
        state.Editor.Undo();
        Assert.AreEqual(original, state.Text);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// A rebind cannot apply an older acceptance after editing, caret movement, or a semantic session mutation.
    /// </summary>
    /// <param name="change">The real editor or session mutation that makes the selection obsolete.</param>
    [TestMethod]
    [DataRow("edit")]
    [DataRow("caret")]
    [DataRow("session")]
    public async Task Acceptance_ChangedContext_DiscardsPendingIntent(string change)
    {
        if (await IsolatedTestProcess.RunAsync(TestContext))
        {
            return;
        }

        const string original = "call Environment::get_CurrentManagedTh";
        await using var engine = new CompletionEngine();
        await engine.PrimeAsync(TestContext.CancellationToken);
        var state = State(engine, original);
        var requester = state.Requester!;
        requester.Refresh(state);
        await ReleaseAsync(engine, state, 0);
        var selected = state.Completions!.Reply.Items.Single();
        CompletionEngine.ChangeAssemblies();
        requester.QueueAcceptance(state, selected);
        await WaitAsync(() =>
        {
            requester.Refresh(state);
            return engine.Calls.Count == 2;
        });
        switch (change)
        {
            case "edit": state.Editor.InsertText("r"); break;
            case "caret": state.Editor.SetCursorPosition(new DocumentOffset(original.Length - 1)); break;
            case "session": await engine.HandleAsync(".clear", TestContext.CancellationToken); break;
        }

        var expected = state.Text;
        var caret = state.Editor.Cursor.Position;
        await engine.Calls[1].Prepared.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        engine.Calls[1].Release.SetResult();
        await WaitAsync(() =>
        {
            requester.Refresh(state);
            return engine.Calls.Count == 3;
        });
        await ReleaseAsync(engine, state, 2);
        Assert.AreEqual(expected, state.Text);
        Assert.AreEqual(caret, state.Editor.Cursor.Position);
        Assert.IsFalse(state.PaletteDismissed);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    private static PromptState State(CompletionEngine engine, string text)
    {
        var state = new PromptState(new PromptHistory(), new CilTokenizer(engine.Vocabulary))
        {
            Requester = new CompletionRequester(engine),
        };
        state.SetText(text, text.Length);
        return state;
    }

    private async Task<CompletionResult> ReleaseAsync(CompletionEngine engine, PromptState state, int index, bool apply = true)
    {
        await engine.Calls[index].Prepared.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        engine.Calls[index].Release.SetResult();
        return await DrainAsync(state, apply);
    }

    private async Task<CompletionResult> DrainAsync(PromptState state, bool apply = true)
    {
        await WaitAsync(() => !state.Events.IsEmpty);
        CompletionResult? completion = null;
        while (state.Events.TryDequeue(out var message))
        {
            if (message.CompletionResult is { } result)
            {
                completion = result;
                if (apply)
                {
                    state.Requester!.Apply(state, result);
                }
            }
        }

        Assert.IsNotNull(completion);
        return completion;
    }

    private async Task WaitAsync(Func<bool> ready)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!ready())
        {
            await Task.Delay(1, deadline.Token);
        }
    }

    private static async Task CloseAsync(CompletionEngine engine, CompletionRequester requester)
    {
        foreach (var call in engine.Calls)
        {
            call.Release.TrySetResult();
        }

        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }
}
