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
        Assert.IsTrue(history.Add("ldc.i4 1\n"));
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
        history.Add(".method int32 F() {\n  ldc.i4 1\n  ret\n}\n");
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
        Assert.IsTrue(await history.AddAsync("ldc.i4 1\n", CancellationToken.None));
        Assert.IsFalse(await history.AddAsync("ldc.i4 1", CancellationToken.None));
        Assert.IsFalse(await history.AddAsync("  ", CancellationToken.None));
        Assert.AreSequenceEqual(["ldc.i4 1"], store.Appended);
    }

    /// <summary>
    /// Loading replaces the entries with the store's, and the store's problem shows through.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task LoadAsync_ReadsTheStore()
    {
        var store = new MemoryHistoryStore();
        store.Stored.AddRange(["one", "two\nlines"]);
        var history = new PromptHistory(store, ["stale"]);
        await history.LoadAsync(CancellationToken.None);
        Assert.AreSequenceEqual(["one", "two\nlines"], history.Entries);
        Assert.AreEqual(1, store.Loads);
        Assert.IsNull(history.Problem);
        store.Problem = "disk full";
        Assert.AreEqual("disk full", history.Problem);
    }
}
