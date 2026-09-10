using Hex1b.Documents;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Exercises request lifetime and complete document identity with independently ordered host replies.
/// </summary>
[TestClass]
public sealed class CompletionRequesterTests
{
    /// <summary>
    /// Supplies cancellation for asynchronous test waits.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A superseded response cannot reopen the palette or provide a committable edit.
    /// </summary>
    [TestMethod]
    public async Task Refresh_OutOfOrderReplies_DropsSupersededRows()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        var stale = Reply(engine, "Write");
        state.Editor.InsertText("i");
        requester.Refresh(state);
        Assert.HasCount(2, engine.Calls);
        Assert.IsTrue(engine.Calls[0].Cancellation.IsCancellationRequested);
        engine.Calls[1].Answer.SetResult(Reply(engine, "WriteLine", length: 12));
        await DrainAsync(state);
        engine.Calls[0].Answer.SetResult(stale);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("WriteLine", state.Completions!.Reply.Items.Single().Name);
        Assert.IsNull(CompletionEdit.For(state, stale.Items[0]), "A stopped requester cannot provide an edit.");
    }

    /// <summary>
    /// Previous rows preserve layout while pending but cannot supply an edit or survive dismissal.
    /// </summary>
    [TestMethod]
    public async Task Refresh_ReplacementPending_KeepsOnlyDisabledDisplayRows()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        engine.Calls[0].Answer.SetResult(Reply(engine, "WriteLine"));
        await DrainAsync(state);
        state.Editor.InsertText("i");
        requester.Refresh(state);
        var display = PromptWidget.DisplayCandidates(state, engine.Catalog);
        Assert.HasCount(1, display);
        Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
        Assert.IsNull(CompletionEdit.For(state, display[0]));
        Assert.AreEqual("call Console::Wri", state.Text);
        requester.Cancel(state);
        Assert.IsNull(state.PendingDisplay);
        Assert.IsEmpty(PromptWidget.DisplayCandidates(state, engine.Catalog));
        await engine.DisposeAsync();
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Empty intermediate pages keep previous rows disabled until the replacement query has finished.
    /// </summary>
    /// <param name="hasMatch">Whether the last page supplies a replacement row.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Refresh_EmptyReplacementPages_PreserveDisabledRows(bool hasMatch)
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        var original = Reply(engine, "WriteLine");
        engine.Calls[0].Answer.SetResult(original);
        await DrainAsync(state);
        state.Editor.InsertText("i");
        requester.Refresh(state);
        for (var page = 1; page <= 2; page++)
        {
            engine.Calls[page].Answer.SetResult(Reply(engine, "WriteLine", length: 12) with
            {
                Items = [], Cursor = "page-" + (page + 1), TotalIsProvisional = true,
            });
            await DrainAsync(state);
            Assert.HasCount(1, PromptWidget.DisplayCandidates(state, engine.Catalog));
            Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
            Assert.IsNull(CompletionEdit.For(state, original.Items[0]));
            requester.Refresh(state);
            Assert.HasCount(1, PromptWidget.DisplayCandidates(state, engine.Catalog));
            Assert.AreEqual("page-" + (page + 1), engine.Calls[page + 1].Request.Cursor);
        }

        var final = Reply(engine, "WriteLine", length: 12);
        engine.Calls[3].Answer.SetResult(hasMatch ? final : final with { Items = [], Total = 0 });
        await DrainAsync(state);
        Assert.IsNull(state.PendingDisplay);
        Assert.HasCount(hasMatch ? 1 : 0, PromptWidget.DisplayCandidates(state, engine.Catalog));
        Assert.HasCount(hasMatch ? 1 : 0, PromptWidget.Candidates(state, engine.Catalog));
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Editing again during an empty intermediate page retains the last visible rows while cancelling that page.
    /// </summary>
    [TestMethod]
    public async Task Refresh_EditDuringEmptyPage_PreservesDisabledRows()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        engine.Calls[0].Answer.SetResult(Reply(engine, "WriteLine"));
        await DrainAsync(state);
        state.Editor.InsertText("i");
        requester.Refresh(state);
        engine.Calls[1].Answer.SetResult(Reply(engine, "WriteLine", length: 12) with { Items = [], Cursor = "next" });
        await DrainAsync(state);
        requester.Refresh(state);
        state.Editor.InsertText("t");
        requester.Refresh(state);
        Assert.IsTrue(engine.Calls[2].Cancellation.IsCancellationRequested);
        Assert.HasCount(1, PromptWidget.DisplayCandidates(state, engine.Catalog));
        Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
        await engine.DisposeAsync();
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Identical redraws retain one request and an explicit retry lifts a pending dismissal.
    /// </summary>
    [TestMethod]
    public async Task Refresh_PendingAndDismissed_OnlyRequestsWhenNeeded()
    {
        await using var engine = new CompletionEngine();
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
        Assert.HasCount(2, engine.Calls);
        Assert.AreEqual(PaletteMode.Requested, state.Palette);
        await engine.DisposeAsync();
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Faults stay quiet until an explicit retry, while cancellation leaves the query retryable.
    /// </summary>
    [TestMethod]
    public async Task Refresh_FaultAndCancellation_ApplyDifferentRetryRules()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        engine.Calls[0].Answer.SetException(new IOException("closed pipe"));
        await DrainAsync(state);
        requester.Refresh(state);
        Assert.AreEqual(PaletteMode.Faulted, state.Palette);
        Assert.HasCount(1, engine.Calls);
        requester.Request(state);
        engine.Calls[1].Answer.SetCanceled(TestContext.CancellationToken);
        await DrainAsync(state);
        Assert.IsNull(requester.LastAnswered);
        requester.Refresh(state);
        Assert.HasCount(3, engine.Calls);
        engine.Calls[2].Answer.SetResult(Reply(engine, "Write"));
        await DrainAsync(state);
        Assert.AreEqual(PaletteMode.Open, state.Palette);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Empty pages are followed and a page in flight survives repeated navigation and redraws.
    /// </summary>
    [TestMethod]
    public async Task Refresh_PagedResults_PreservesCursorAndQueryStamps()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        engine.Calls[0].Answer.SetResult(Reply(engine, "Write") with { Items = [], Cursor = "page-2", TotalIsProvisional = true });
        await DrainAsync(state);
        requester.Refresh(state);
        Assert.AreEqual("page-2", engine.Calls[1].Request.Cursor);
        requester.RequestMore(state);
        requester.Refresh(state);
        Assert.HasCount(2, engine.Calls);
        engine.Calls[1].Answer.SetResult(Reply(engine, "Write") with { Cursor = "page-3", TotalIsProvisional = true });
        await DrainAsync(state);
        requester.RequestMore(state);
        requester.Refresh(state);
        Assert.AreEqual("page-3", engine.Calls[2].Request.Cursor);
        engine.Calls[2].Answer.SetResult(Reply(engine, "WriteLine") with { Total = 2 });
        await DrainAsync(state);
        Assert.HasCount(2, PromptWidget.Candidates(state, engine.Catalog));
        Assert.IsFalse(state.MoreCompletions);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Revision changes invalidate accepted rows even when document text and session counts stay the same.
    /// </summary>
    [TestMethod]
    public async Task Refresh_CommentMutation_RequeriesAndRefusesOldEdits()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        engine.Immediate = Reply(engine, "Write");
        state.Requester!.Refresh(state);
        Assert.IsNotNull(CompletionEdit.For(state, engine.Immediate.Items[0]));
        await engine.HandleAsync("/*", TestContext.CancellationToken);
        Assert.IsNull(CompletionEdit.For(state, engine.Immediate.Items[0]));
        await engine.HandleAsync("*/", TestContext.CancellationToken);
        engine.Immediate = Reply(engine, "Write");
        state.Requester.Refresh(state);
        Assert.HasCount(2, engine.Calls);
        Assert.IsNotNull(CompletionEdit.For(state, engine.Immediate.Items[0]));
        await state.Requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Empty type sites wait for Tab and a synchronous answer is usable in the same frame.
    /// </summary>
    [TestMethod]
    public async Task Refresh_EmptyBroadSite_WaitsForExplicitRequest()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call ");
        state.Requester!.Refresh(state);
        Assert.IsEmpty(engine.Calls);
        engine.Immediate = Reply(engine, "Console") with { Kind = CompletionKind.Types, ReplaceLength = 0 };
        state.Requester.Request(state);
        Assert.HasCount(1, engine.Calls);
        Assert.AreEqual(PaletteMode.Open, state.Palette);
        Assert.IsFalse(state.Requester.IsPending);
        await state.Requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// A cursor move invalidates rows before the next frame and resets the palette's navigation.
    /// </summary>
    [TestMethod]
    public async Task Refresh_CaretMove_InvalidatesRowsImmediately()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        engine.Immediate = Reply(engine, "Write");
        state.Requester!.Refresh(state);
        state.SelectedIndex = 7;
        state.PaletteNavigated = true;
        state.DetailScroll = 9;
        state.Editor.SetCursorPosition(new DocumentOffset(14));
        Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
        engine.Immediate = null;
        state.Requester.Refresh(state);
        Assert.AreEqual(0, state.SelectedIndex);
        Assert.AreEqual(0, state.DetailScroll);
        Assert.IsFalse(state.PaletteNavigated);
        await engine.DisposeAsync();
        await state.Requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Busy submission cancels preview work and the same document is queried again after settlement.
    /// </summary>
    [TestMethod]
    public async Task Refresh_BusySubmission_RetriesWhenSettled()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        state.Requester!.Refresh(state);
        state.Submission = new Submission(engine, [], 0, false, _ => Task.CompletedTask, _ => { });
        state.Requester.Refresh(state);
        Assert.IsTrue(engine.Calls[0].Cancellation.IsCancellationRequested);
        Assert.IsEmpty(PromptWidget.Candidates(state, engine.Catalog));
        state.Requester.Refresh(state);
        Assert.HasCount(1, engine.Calls);
        await state.Submission.Completion;
        state.Submission = null;
        engine.Immediate = Reply(engine, "Write");
        state.Requester.Refresh(state);
        Assert.HasCount(2, engine.Calls);
        Assert.AreEqual(PaletteMode.Open, state.Palette);
        engine.Calls[0].Answer.SetResult(Reply(engine, "Old"));
        await state.Requester.SettleAsync(TimeSpan.FromSeconds(2));
        Assert.IsEmpty(state.Events);
    }

    /// <summary>
    /// Shutdown settles superseded tasks before another requester starts on the same engine.
    /// </summary>
    [TestMethod]
    public async Task Settle_SupersededTasks_CannotPublishIntoTheNextSession()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        state.Requester!.Refresh(state);
        state.Editor.InsertText("i");
        state.Requester.Refresh(state);
        var settling = state.Requester.SettleAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(settling.IsCompleted);
        Assert.IsTrue(engine.Calls.All(call => call.Cancellation.IsCancellationRequested));
        foreach (var call in engine.Calls)
        {
            call.Answer.SetResult(Reply(engine, "Old"));
        }

        await settling;
        var replacement = State(engine, "call Console::Wr");
        engine.Immediate = Reply(engine, "New");
        replacement.Requester!.Refresh(replacement);
        Assert.AreEqual("New", replacement.Completions!.Reply.Items.Single().Name);
        Assert.IsEmpty(state.Events);
        Assert.IsEmpty(replacement.Events);
        await replacement.Requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Background assembly loads remove cached edits and wake an idle prompt without changing its document or session.
    /// </summary>
    [TestMethod]
    public async Task Refresh_AssemblyLoad_InvalidatesCachedRowsAndRequeries()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        engine.Immediate = Reply(engine, "Old");
        requester.Refresh(state);
        var old = state.Completions!;
        state.PaletteNavigated = true;
        Assert.IsNotNull(CompletionEdit.For(state, old.Reply.Items[0]));
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Invalidate = () => invalidated.TrySetResult();
        engine.ChangeAssemblies();
        Assert.IsFalse(requester.Matches(state, old));
        Assert.IsNull(CompletionEdit.For(state, old.Reply.Items[0]));
        Assert.AreEqual(EnterAction.AcceptCompletion,
            PromptWidget.EnterActionFor(state, paletteVisible: false, openDepth: 0, commentOpen: false));
        state.PaletteDismissed = true;
        Assert.AreEqual(EnterAction.Submit,
            PromptWidget.EnterActionFor(state, paletteVisible: false, openDepth: 0, commentOpen: false));
        state.PaletteDismissed = false;
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
        engine.Immediate = Reply(engine, "Qualified");
        requester.Refresh(state);
        Assert.HasCount(2, engine.Calls);
        Assert.AreEqual("Qualified", state.Completions!.Reply.Items.Single().Name);
        Assert.AreEqual(old.Key.Document, state.Completions.Key.Document);
        Assert.AreEqual(old.Key.Revision, state.Completions.Key.Revision);
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
        await requester.SettleAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// An assembly change while a page is pending makes its old reply ineligible for publication.
    /// </summary>
    [TestMethod]
    public async Task Apply_AssemblyLoad_DropsThePendingReply()
    {
        await using var engine = new CompletionEngine();
        var state = State(engine, "call Console::Wr");
        var requester = state.Requester!;
        requester.Refresh(state);
        var old = Reply(engine, "Old");
        engine.ChangeAssemblies();
        engine.Calls[0].Answer.SetResult(old);
        await DrainAsync(state);
        Assert.IsNull(state.Completions);
        Assert.IsNull(requester.LastAnswered);
        engine.Immediate = Reply(engine, "Qualified");
        requester.Refresh(state);
        Assert.HasCount(2, engine.Calls);
        Assert.AreEqual("Qualified", state.Completions!.Reply.Items.Single().Name);
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

    private static CompletionReply Reply(CompletionEngine engine, string name, int length = 11) =>
        new(CompletionKind.Members, 5, length,
            [new CompletionItem(name, "[] → void", "", false) { Kind = CompletionKind.Members, Insert = "Console::" + name + "()" }],
            null, 1, false, engine.Status.Revision, "query", 1, []) { AssemblyVersion = engine.AssemblyVersion };

    private async Task DrainAsync(PromptState state)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        while (state.Events.IsEmpty)
        {
            await Task.Delay(1, deadline.Token);
        }

        while (state.Events.TryDequeue(out var message))
        {
            if (message.CompletionResult is { } result)
            {
                state.Requester!.Apply(state, result);
            }
        }
    }
}
