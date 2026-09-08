using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests for <see cref="PromptHistory"/>: entries, working copies, and the store behind them.
/// </summary>
[TestClass]
public sealed class PromptHistoryTests
{
    /// <summary>
    /// Whitespace and a repeat of the newest entry are not added; trailing newlines are trimmed.
    /// </summary>
    [TestMethod]
    public void Add_RefusesWhitespaceAndRepeats()
    {
        var history = new PromptHistory();
        Assert.IsTrue(history.Add("ldc.i4 1"));
        Assert.IsFalse(history.Add("ldc.i4 1"));
        Assert.IsFalse(history.Add("   "));
        Assert.IsFalse(history.Add(""));
        Assert.IsTrue(history.Add("ldc.i4 2"));
        Assert.IsTrue(history.Add("ldc.i4 1"));
        Assert.AreSequenceEqual(["ldc.i4 1", "ldc.i4 2", "ldc.i4 1"], history.Entries);
    }

    /// <summary>
    /// A block is one entry with its lines.
    /// </summary>
    [TestMethod]
    public void Add_KeepsABlockAsOneEntry()
    {
        var history = new PromptHistory();
        history.Add(".method int32 F() {\n  ldc.i4 1\n  ret\n}");
        Assert.HasCount(1, history.Entries);
        Assert.AreEqual(".method int32 F() {\n  ldc.i4 1\n  ret\n}", history.Entries[0]);
    }

    /// <summary>
    /// Going back stashes the buffer at the newest end, and coming forward past the newest entry brings it back.
    /// </summary>
    [TestMethod]
    public void Back_StashesInProgressBuffer_ForwardRestoresIt()
    {
        var history = new PromptHistory(entries: ["one", "two"]);
        Assert.AreEqual("two", history.Back("typing"));
        Assert.IsTrue(history.Browsing);
        Assert.AreEqual("one", history.Back("two"));
        Assert.IsNull(history.Back("one"));
        Assert.AreEqual("two", history.Forward("one"));
        Assert.AreEqual("typing", history.Forward("two"));
        Assert.IsFalse(history.Browsing);
        Assert.IsNull(history.Forward("typing"));
    }

    /// <summary>
    /// Editing a recalled entry edits its working copy; the entry itself is unchanged, and the
    /// edit is still there when the user comes back to it.
    /// </summary>
    [TestMethod]
    public void Back_EditedRecall_KeepsWorkingCopyNotEntry()
    {
        var history = new PromptHistory(entries: ["one", "two"]);
        history.Back("");
        history.Back("two edited");
        Assert.AreEqual("two edited", history.Forward("one"));
        Assert.AreEqual("two", history.Entries[1]);
        history.Reset();
        Assert.AreEqual("two", history.Back(""));
    }

    /// <summary>
    /// Adding ends browsing and drops the working copies.
    /// </summary>
    [TestMethod]
    public void Add_EndsBrowsing()
    {
        var history = new PromptHistory(entries: ["one"]);
        history.Back("draft");
        Assert.IsTrue(history.Browsing);
        history.Add("three");
        Assert.IsFalse(history.Browsing);
        Assert.AreEqual("three", history.Back(""));
        Assert.AreEqual("one", history.Back("three"));
    }

    /// <summary>
    /// Only the last thousand entries are kept in memory.
    /// </summary>
    [TestMethod]
    public void Add_KeepsTheLastThousand()
    {
        var history = new PromptHistory();
        for (var i = 0; i < 1005; i++)
        {
            history.Add("entry " + i);
        }

        Assert.HasCount(PromptHistory.MaxEntries, history.Entries);
        Assert.AreEqual("entry 5", history.Entries[0]);
        Assert.AreEqual("entry 1004", history.Entries[^1]);
    }

    /// <summary>
    /// A new entry is written to the store before the add completes; a repeat is not.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task AddAsync_WritesNewEntriesToTheStore()
    {
        var store = new MemoryHistoryStore();
        var history = new PromptHistory(store);
        Assert.IsTrue(await history.AddAsync("ldc.i4 1", CancellationToken.None));
        Assert.IsFalse(await history.AddAsync("ldc.i4 1", CancellationToken.None));
        Assert.IsFalse(await history.AddAsync("  ", CancellationToken.None));
        Assert.IsTrue(await history.AddAsync("ldc.i4 1\n", CancellationToken.None), "a trailing blank line is a run, so this is another entry");
        Assert.AreSequenceEqual(["ldc.i4 1", "ldc.i4 1\n"], store.Appended);
    }

    /// <summary>
    /// Loading puts the store's entries before the ones this session already has, and the
    /// store's problem shows through.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task LoadAsync_ReadsTheStore()
    {
        var store = new MemoryHistoryStore();
        store.Stored.AddRange(["one", "two\nlines"]);
        var history = new PromptHistory(store, ["added meanwhile"]);
        await history.LoadAsync(CancellationToken.None);
        Assert.AreSequenceEqual(["one", "two\nlines", "added meanwhile"], history.Entries);
        Assert.AreEqual(1, store.Loads);
        Assert.IsNull(history.Problem);
        store.Problem = "disk full";
        Assert.AreEqual("disk full", history.Problem);
    }

    /// <summary>
    /// The store's writes before this session began are not the session's, however many there
    /// were: a line written after the read was asked for stays recallable once the read lands.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_CountsOnlyThisSessionsWrites()
    {
        var store = new MemoryHistoryStore { HoldLoad = new TaskCompletionSource() };
        store.Appended.Add("earlier");
        var history = new PromptHistory(store);
        var load = history.LoadAsync(CancellationToken.None);
        Assert.IsTrue(await history.AddAsync("nop", CancellationToken.None));
        store.HoldLoad.SetResult();
        await load;
        Assert.AreSequenceEqual(["earlier", "nop"], history.Entries);

        var settled = new MemoryHistoryStore();
        settled.Appended.Add("earlier");
        var again = new PromptHistory(settled);
        Assert.IsTrue(await again.AddAsync("nop", CancellationToken.None));
        await again.LoadAsync(CancellationToken.None);
        Assert.AreSequenceEqual(["earlier", "nop"], again.Entries, "a write the read holds is not added again");
    }

    /// <summary>
    /// An entry added before the store answered stays, after the stored ones, and one the store
    /// had already taken is not doubled.
    /// </summary>
    [TestMethod]
    public void Load_KeepsEntriesAddedMeanwhile()
    {
        var history = new PromptHistory();
        Assert.IsTrue(history.Add("nop"));
        Assert.IsTrue(history.Add("ldc.i4 2"));
        history.Load(["ldc.i4 1", "nop"], own: 1);
        Assert.AreSequenceEqual(["ldc.i4 1", "nop", "ldc.i4 2"], history.Entries);
        Assert.AreEqual("ldc.i4 2", history.Back(""));
        Assert.AreEqual("nop", history.Back("ldc.i4 2"));
        Assert.AreEqual("ldc.i4 1", history.Back("nop"));

        var written = new PromptHistory();
        written.Add("nop");
        written.Load(["nop"], own: 1);
        Assert.AreSequenceEqual(["nop"], written.Entries, "an entry the store had already taken is not added again");
    }

    /// <summary>
    /// A load that lands while browsing keeps the place, the working copies, and the draft the
    /// buffer held when browsing began; the stored entries are reachable further back.
    /// </summary>
    [TestMethod]
    public void Load_WhileBrowsing_KeepsDraftAndPosition()
    {
        var history = new PromptHistory();
        Assert.IsTrue(history.Add("nop"));
        Assert.AreEqual("nop", history.Back("ldc.i4 42"));
        history.Load(["ldc.i4 1", "ldc.i4 2"]);
        Assert.IsTrue(history.Browsing);
        Assert.AreEqual("ldc.i4 42", history.Forward("nop edited"), "Down brings the draft back and ends browsing");
        Assert.AreEqual("nop", history.Back("ldc.i4 42"), "browsing starts again from the draft");
        Assert.AreEqual("ldc.i4 2", history.Back("nop"));
        Assert.AreEqual("ldc.i4 1", history.Back("ldc.i4 2"));
        Assert.IsNull(history.Back("ldc.i4 1"));
        Assert.AreSequenceEqual(["ldc.i4 1", "ldc.i4 2", "nop"], history.Entries);

        // A working copy edited before the load is still there afterwards, until browsing ends.
        var edited = new PromptHistory();
        edited.Add("nop");
        edited.Add("ldc.i4 2");
        edited.Back("draft");
        edited.Back("ldc.i4 2 edited");
        edited.Load(["ldc.i4 1"]);
        Assert.AreEqual("ldc.i4 2 edited", edited.Forward("nop"), "the working copy survived the load");
        Assert.AreEqual("draft", edited.Forward("ldc.i4 2 edited"));

        // Browsing the draft itself when the load lands keeps it too.
        var atDraft = new PromptHistory();
        atDraft.Add("nop");
        atDraft.Back("draft");
        atDraft.Forward("nop");
        atDraft.Load(["ldc.i4 1"]);
        Assert.AreEqual("nop", atDraft.Back("draft"), "Up from the draft recalls the newest entry");
        Assert.AreEqual("draft", atDraft.Forward("nop"));
    }

    /// <summary>
    /// A buffer that ends with a blank line ends with a run: the entry keeps that line, so the
    /// recalled entry does what the original did; a buffer of nothing but blank lines is no entry.
    /// </summary>
    [TestMethod]
    public void Add_KeepsATrailingRunLine()
    {
        var history = new PromptHistory();
        Assert.IsTrue(history.Add("ldc.i4.1\n"));
        Assert.AreEqual("ldc.i4.1\n", history.Entries[^1]);
        Assert.IsFalse(history.Add("ldc.i4.1\n"));
        Assert.IsFalse(history.Add("\n\n"));
        Assert.IsTrue(history.Add("ldc.i4.1"));
        Assert.AreEqual("ldc.i4.1", history.Back(""));
        Assert.AreEqual("ldc.i4.1\n", history.Back("ldc.i4.1"), "the entry with the run line is its own entry");
    }

    /// <summary>
    /// The session's own writes the store had taken come back at its end and are not added
    /// again; a stored run that merely reads the same is another run and stays recallable.
    /// </summary>
    [TestMethod]
    public void Load_TakesItsOwnWritesFromTheStoredEnd()
    {
        var history = new PromptHistory();
        history.Add("a");
        history.Add("b");
        history.Load(["x", "a", "b"], own: 2);
        Assert.AreSequenceEqual(["x", "a", "b"], history.Entries);

        var partial = new PromptHistory();
        partial.Add("a");
        partial.Add("b");
        partial.Load(["x", "a"], own: 1);
        Assert.AreSequenceEqual(["x", "a", "b"], partial.Entries);

        var none = new PromptHistory();
        none.Add("a");
        none.Load(["b"], own: 0);
        Assert.AreSequenceEqual(["b", "a"], none.Entries);

        var coincidence = new PromptHistory();
        coincidence.Add("a");
        coincidence.Add("b");
        coincidence.Load(["x", "a", "b"], own: 0);
        Assert.AreSequenceEqual(["x", "a", "b", "a", "b"], coincidence.Entries, "the same text written twice is two entries");

        // Browsing keeps its place through the merge when the entries move.
        var browsing = new PromptHistory();
        browsing.Add("a");
        browsing.Add("b");
        Assert.AreEqual("b", browsing.Back("draft"));
        Assert.AreEqual("a", browsing.Back("b"));
        browsing.Load(["x", "a", "b"], own: 2);
        Assert.AreEqual("x", browsing.Back("a"));
        Assert.AreEqual("a", browsing.Forward("x"));
        Assert.AreEqual("b", browsing.Forward("a"));
        Assert.AreEqual("draft", browsing.Forward("b"));
    }
}
