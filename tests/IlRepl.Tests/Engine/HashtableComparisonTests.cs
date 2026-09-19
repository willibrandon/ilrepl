using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real hashtable observations preserve logical contents, comparer behavior, wrapper identity, and bounded snapshots.
/// </summary>
[TestClass]
public sealed class HashtableComparisonTests
{
    /// <summary>
    /// Supplies cancellation for actual workers and the concurrent mutation test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Independent processes match reordered tables and distinguish revised values across plain and synchronized forms.
    /// </summary>
    /// <param name="count">The original entry count.</param>
    /// <param name="wrappers">The number of synchronized wrappers.</param>
    /// <param name="comparer">The actual comparer configuration.</param>
    [TestMethod]
    [DataRow(0, 0, "default")]
    [DataRow(1, 0, "default")]
    [DataRow(8, 0, "default")]
    [DataRow(8, 1, "default")]
    [DataRow(8, 2, "OrdinalIgnoreCase")]
    [DataRow(8, 0, "InvariantCultureIgnoreCase")]
    [DataRow(8, 1, "legacy")]
    public async Task Compare_HashtableContentsIgnoreRandomizedStorage(int count, int wrappers, string comparer)
    {
        var session = IlLines.Load(HashtableComparisonExamples.Method(count, wrappers, comparer).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        Assert.IsEmpty(edit.Problems);
        session.CommitEdit(edit.Name, HashtableComparisonExamples.Method(count, wrappers, comparer, reverse: true));
        foreach (var method in new[] { edit.Original.Requested, edit.OriginalMethod, edit.Method! })
        {
            AssertActual(method.Invoke(null, null), count, false, wrappers);
        }

        var same = await CompareAsync(session);
        Assert.AreEqual("match", same.Outcome, Details(same));
        AssertContents(same.Original.Result!, count, false, wrappers);
        AssertContents(same.Edited.Result!, count, false, wrappers);
        session.CommitEdit(edit.Name, HashtableComparisonExamples.Method(count, wrappers, comparer, edited: true));
        AssertActual(edit.Method!.Invoke(null, null), count, true, wrappers);
        var changed = await CompareAsync(session);
        Assert.AreEqual("different", changed.Outcome, Details(changed));
        AssertContents(changed.Original.Result!, count, false, wrappers);
        AssertContents(changed.Edited.Result!, count, true, wrappers);
    }

    /// <summary>
    /// Comparer-only edits remain visible for empty tables and populated synchronized wrappers.
    /// </summary>
    /// <param name="count">The original entry count.</param>
    /// <param name="wrappers">The synchronized wrapper count.</param>
    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(8, 1)]
    public async Task Compare_HashtableComparerSettingsRemainObservable(int count, int wrappers)
    {
        var session = IlLines.Load(HashtableComparisonExamples.Method(count, wrappers, "Ordinal").Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, HashtableComparisonExamples.Method(count, wrappers, "OrdinalIgnoreCase"));
        var result = await CompareAsync(session);
        Assert.AreEqual("different", result.Outcome, Details(result));
        AssertContents(result.Original.Result!, count, false, wrappers);
        AssertContents(result.Edited.Result!, count, false, wrappers);
        Assert.AreEqual("ordinal", Unwrap(result.Original.Result!, wrappers).Members[0].Value.Value);
        Assert.AreEqual("ordinal-ignore-case", Unwrap(result.Edited.Result!, wrappers).Members[0].Value.Value);
    }

    /// <summary>
    /// Capacity, insertion order, removed buckets and cached views do not affect logical observations.
    /// </summary>
    [TestMethod]
    public void Capture_HashtableIgnoresUnusedStorage()
    {
        var first = new Hashtable(1, StringComparer.OrdinalIgnoreCase);
        var second = new Hashtable(1000, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in HashtableComparisonExamples.Contents(8))
        {
            first.Add(entry.Key, entry.Value);
        }

        foreach (var entry in HashtableComparisonExamples.Contents(8).Reverse())
        {
            second.Add(entry.Key, entry.Value);
        }

        second.Add("removed", 99);
        second.Remove("removed");
        _ = second.Keys;
        _ = second.Values;
        var observed = Observe(first);
        Assert.AreEqual(observed, Observe(second));
        AssertContents(observed, 8, false, 0);
    }

    /// <summary>
    /// Backing tables, nested wrappers, self cycles and repeated roots retain distinct identities.
    /// </summary>
    [TestMethod]
    public void Capture_HashtableWrappersPreserveBackingAliasesAndCycles()
    {
        var table = new Hashtable();
        var first = Hashtable.Synchronized(table);
        var second = Hashtable.Synchronized(table);
        var nested = Hashtable.Synchronized(first);
        var shared = new object();
        table.Add("self", table);
        table.Add("wrapper", first);
        table.Add("first", shared);
        table.Add("second", shared);
        table.Add("null", null);
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var observed = observer.Capture(nested);
        var wrapper = Unwrap(observed, 1);
        var backing = Unwrap(observed, 2);
        Assert.AreNotEqual(observed.Identity, wrapper.Identity);
        Assert.AreNotEqual(wrapper.Identity, backing.Identity);
        var entries = Entries(backing);
        Assert.HasCount(5, entries);
        Assert.AreEqual("reference", entries["self"].Kind);
        Assert.AreEqual(backing.Identity, entries["self"].Identity);
        Assert.AreEqual("reference", entries["wrapper"].Kind);
        Assert.AreEqual(wrapper.Identity, entries["wrapper"].Identity);
        Assert.AreEqual(entries["first"].Identity, entries["second"].Identity);
        Assert.AreEqual("null", entries["null"].Kind);
        var another = observer.Capture(second);
        Assert.AreNotEqual(wrapper.Identity, another.Identity);
        Assert.AreEqual("reference", another.Members[0].Value.Kind);
        Assert.AreEqual(backing.Identity, another.Members[0].Value.Identity);
        Assert.AreEqual(backing.Identity, observer.Capture(table).Identity);
        Assert.AreEqual(entries["first"].Identity, observer.Capture(shared).Identity);
        table["second"] = new object();
        Assert.AreNotEqual(observed, Observe(nested));
    }

    /// <summary>
    /// Derived fields survive direct and synchronized capture without virtual getter, copy or enumeration calls.
    /// </summary>
    /// <param name="wrapped">Whether to wrap the actual subclass before observing it.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Capture_HashtableSubclassNeverCallsUserOverrides(bool wrapped)
    {
        var item = new ComparisonObservedNode();
        var table = new HashtableObservationSubclass { Extra = item };
        table.Add("first", item);
        var observed = Unwrap(Observe(wrapped ? Hashtable.Synchronized(table) : table), wrapped ? 1 : 0);
        Assert.EndsWith("HashtableObservationSubclass", observed.Type);
        Assert.HasCount(3, observed.Members);
        var extra = observed.Members.Single(member => member.Name.EndsWith("::Extra", StringComparison.Ordinal)).Value;
        Assert.AreEqual("object", extra.Kind);
        Assert.AreEqual("reference", Entries(observed)["first"].Kind);
        Assert.AreEqual(extra.Identity, Entries(observed)["first"].Identity);
        Assert.AreEqual(0, item.UserCodeCalls);
    }

    /// <summary>
    /// Custom comparer fields and value aliases remain visible without user equality, hashing or formatting.
    /// </summary>
    [TestMethod]
    public void Capture_HashtableComparerNeverCallsUserCode()
    {
        var comparer = new HashtableObservationComparer();
        var table = new Hashtable(comparer) { ["first"] = comparer, ["second"] = comparer };
        Assert.IsGreaterThan(0, comparer.Calls);
        comparer.Calls = 0;
        comparer.Reject = true;
        var observed = Observe(table);
        var captured = observed.Members[0].Value;
        Assert.AreEqual("object", captured.Kind);
        Assert.AreEqual("17", captured.Members.Single(member => member.Name.EndsWith("::Salt", StringComparison.Ordinal)).Value.Value);
        foreach (var entry in Entries(observed).Values)
        {
            Assert.AreEqual("reference", entry.Kind);
            Assert.AreEqual(captured.Identity, entry.Identity);
        }

        comparer.Salt = 19;
        Assert.AreNotEqual(observed, Observe(table));
        Assert.AreEqual(0, comparer.Calls);
    }

    /// <summary>
    /// Legacy comparer adapters retain both culture settings without exposing native globalization state.
    /// </summary>
    /// <param name="ignoreCase">Whether the legacy comparer ignores case independently of its hash provider.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Capture_HashtableLegacyComparerPreservesCulture(bool ignoreCase)
    {
        var first = LegacyTable("en-US", ignoreCase);
        var second = LegacyTable("tr-TR", ignoreCase);
        first.Add("value", 42);
        second.Add("value", 42);
        var observed = Observe(first);
        var comparer = observed.Members[0].Value;
        Assert.AreEqual("object", comparer.Kind);
        Assert.EndsWith("System.Collections.CompatibleComparer", comparer.Type);
        Assert.HasCount(2, comparer.Members);
        foreach (var member in comparer.Members)
        {
            Assert.AreEqual("comparer", member.Value.Kind);
            var options = !ignoreCase && member.Name.EndsWith("::_comparer", StringComparison.Ordinal) ? 0 : 1;
            Assert.AreEqual("en-US:" + options.ToString(CultureInfo.InvariantCulture), member.Value.Value);
            Assert.IsEmpty(member.Value.Members);
        }

        Assert.ContainsSingle(comparer.Members.Where(member => member.Name.EndsWith("::_comparer", StringComparison.Ordinal)));
        Assert.ContainsSingle(comparer.Members.Where(member => member.Name.EndsWith("::_hcp", StringComparison.Ordinal)));
        Assert.AreEqual("42", Entries(observed)["value"].Value);
        Assert.AreNotEqual(observed, Observe(second));
    }

    /// <summary>
    /// Structurally distinct keys are supported while indistinguishable reference keys fail explicitly.
    /// </summary>
    [TestMethod]
    public void Capture_HashtableObjectKeysRemainStructuralAndBounded()
    {
        var first = new ComparisonObservedNode { Name = "first" };
        var second = new ComparisonObservedNode { Name = "second" };
        var table = new Hashtable(ReferenceEqualityComparer.Instance) { [first] = 42, [second] = 43 };
        var observed = Observe(table);
        Assert.HasCount(3, observed.Members);
        var entries = observed.Members.Skip(1).Select(member => member.Value.Members).ToDictionary(
            pair => pair[0].Value.Members.Single(member => member.Name.EndsWith("::Name", StringComparison.Ordinal)).Value.Value!,
            pair => pair[1].Value.Value);
        Assert.AreEqual("42", entries["first"]);
        Assert.AreEqual("43", entries["second"]);
        Assert.AreEqual(0, first.UserCodeCalls);
        Assert.AreEqual(0, second.UserCodeCalls);
        table = new Hashtable(ReferenceEqualityComparer.Instance) { [new object()] = 42 };
        var singleton = Observe(table);
        Assert.HasCount(2, singleton.Members);
        Assert.AreEqual("42", singleton.Members[1].Value.Members[1].Value.Value);
        table.Add(new object(), 43);
        AssertUnavailable(Observe(table), "distinct collection keys have indistinguishable structural order");
    }

    /// <summary>
    /// Output and structural ordering limits retain smaller tables and reject oversized tables with explicit reasons.
    /// </summary>
    /// <param name="kind">Entries, aggregate array-key nodes, or aggregate string-key text.</param>
    /// <param name="count">The number of entries to construct.</param>
    /// <param name="limited">Whether the selected budget is exceeded.</param>
    [TestMethod]
    [DataRow("entries", 1000, false)]
    [DataRow("entries", 5000, true)]
    [DataRow("nodes", 300, false)]
    [DataRow("nodes", 500, true)]
    [DataRow("text", 15, false)]
    [DataRow("text", 17, true)]
    public void Capture_HashtableLimitsRemainExplicit(string kind, int count, bool limited)
    {
        var table = new Hashtable();
        for (var index = 0; index < count; index++)
        {
            object key = kind == "entries" ? index : kind == "nodes" ? new[] { index, 0, 0, 0, 0, 0, 0, 0 }
                : new string((char)('a' + index), 65_500);
            table.Add(key, index);
        }

        var observed = Observe(table);
        if (limited)
        {
            AssertUnavailable(observed, kind == "entries" ? "collection exceeds the observation limit"
                : "collection keys cannot be ordered within the observation limit");
        }
        else
        {
            Assert.HasCount(count + 1, observed.Members);
            var entries = observed.Members.Skip(1).Select(member => member.Value.Members).ToArray();
            foreach (var pair in entries)
            {
                var number = int.Parse(pair[1].Value.Value!, CultureInfo.InvariantCulture);
                Assert.IsInRange(0, count - 1, number);
                if (kind == "nodes")
                {
                    Assert.AreEqual("array", pair[0].Value.Kind);
                    Assert.HasCount(8, pair[0].Value.Members);
                    Assert.AreEqual(pair[1].Value.Value, pair[0].Value.Members[0].Value.Value);
                }
                else
                {
                    Assert.AreEqual(kind == "text" ? new string((char)('a' + number), 65_500) : pair[1].Value.Value,
                        pair[0].Value.Value);
                }
            }

            var actual = entries.Select(pair => int.Parse(pair[1].Value.Value!, CultureInfo.InvariantCulture));
            Assert.AreSequenceEqual(Enumerable.Range(0, count), actual.Order());
        }
    }

    /// <summary>
    /// Real concurrent writes yield valid logical snapshots or explicit mutation reports without depending on scheduling.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Capture_HashtableDuringRealWritesNeverInventsEntries()
    {
        var table = new Hashtable();
        for (var index = 0; index < 32; index++)
        {
            table.Add(index, index * 2);
        }

        AssertIntegerPairs(Observe(table));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var started = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            started.Set();
            while (!cancellation.IsCancellationRequested)
            {
                for (var index = 0; index < 32; index++)
                {
                    table[index] = index * 2;
                    table.Remove((index + 16) % 32);
                }
            }
        }, TestContext.CancellationToken);

        try
        {
            started.Wait(TestContext.CancellationToken);
            for (var index = 0; index < 50; index++)
            {
                var observed = Observe(table);
                if (observed.Members[^1].Name == "remaining")
                {
                    AssertUnavailable(observed, "collection changed during observation");
                }
                else
                {
                    AssertIntegerPairs(observed);
                }
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            await writer;
        }

        var stable = Observe(table);
        AssertIntegerPairs(stable);
        Assert.AreEqual(table.Count, stable.Members.Count - 1);
    }

    /// <summary>
    /// Standard wrappers containing a hashtable inherit logical child capture without special public API calls.
    /// </summary>
    [TestMethod]
    public void Capture_HashtableChildrenFixAdjacentWrapperStorage()
    {
        var first = new StringDictionary();
        var second = new StringDictionary();
        var left = new HybridDictionary(20);
        var right = new HybridDictionary(20);
        foreach (var pair in HashtableComparisonExamples.Contents(8))
        {
            first.Add(pair.Key, pair.Value.ToString(CultureInfo.InvariantCulture));
            left.Add(pair.Key, pair.Value);
        }

        foreach (var pair in HashtableComparisonExamples.Contents(8).Reverse())
        {
            second.Add(pair.Key, pair.Value.ToString(CultureInfo.InvariantCulture));
            right.Add(pair.Key, pair.Value);
        }

        foreach (var (original, reordered) in new[] { ((object)first, (object)second), (left, right) })
        {
            var observed = Observe(original);
            Assert.AreEqual(observed, Observe(reordered));
            var table = Assert.ContainsSingle(observed.Members.Where(member => member.Value.Kind == "dictionary")).Value;
            Assert.HasCount(8, Entries(table));
        }
    }

    private static Hashtable LegacyTable(string culture, bool ignoreCase)
    {
        var type = typeof(CaseInsensitiveComparer).Assembly.GetType("System.Collections.CaseInsensitiveHashCodeProvider")!;
        var information = CultureInfo.GetCultureInfo(culture);
        var provider = Activator.CreateInstance(type, information);
        var contract = type.GetInterfaces().Single(item => item.FullName == "System.Collections.IHashCodeProvider");
        IComparer comparer = ignoreCase ? new CaseInsensitiveComparer(information) : new Comparer(information);
        return (Hashtable)typeof(Hashtable).GetConstructor([contract, typeof(IComparer)])!
            .Invoke([provider, comparer]);
    }

    private static void AssertIntegerPairs(ObservedValue value)
    {
        Assert.AreEqual("dictionary", value.Kind);
        var keys = new HashSet<int>();
        foreach (var member in value.Members.Skip(1))
        {
            Assert.AreEqual("entry", member.Value.Kind);
            var pair = member.Value.Members;
            var key = int.Parse(pair[0].Value.Value!, CultureInfo.InvariantCulture);
            Assert.IsInRange(0, 31, key);
            Assert.IsTrue(keys.Add(key));
            Assert.AreEqual((key * 2).ToString(CultureInfo.InvariantCulture), pair[1].Value.Value);
        }
    }

    private async Task<ComparisonReply> CompareAsync(Session session)
    {
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.IsNotNull(side.Result);
            var invocation = Assert.ContainsSingle(side.Invocations);
            Assert.IsNull(invocation.Exception);
            Assert.AreEqual(side.Result, invocation.Outputs.Single(member => member.Name == "return").Value);
        }

        return result;
    }

    private static void AssertActual(object? value, int count, bool edited, int wrappers)
    {
        var table = Assert.IsInstanceOfType<Hashtable>(value);
        Assert.AreEqual(wrappers > 0, table.IsSynchronized);
        var expected = HashtableComparisonExamples.Contents(count, edited);
        Assert.HasCount(expected.Count, table);
        foreach (var pair in expected)
        {
            Assert.AreEqual(pair.Value, table[pair.Key]);
        }
    }

    private static void AssertContents(ObservedValue value, int count, bool edited, int wrappers)
    {
        var table = Unwrap(value, wrappers);
        Assert.AreEqual("dictionary", table.Kind);
        Assert.EndsWith("System.Collections.Hashtable", table.Type);
        var expected = HashtableComparisonExamples.Contents(count, edited);
        Assert.HasCount(expected.Count + 1, table.Members);
        Assert.AreEqual("comparer", table.Members[0].Name);
        var actual = Entries(table);
        Assert.HasCount(expected.Count, actual);
        foreach (var pair in expected)
        {
            Assert.AreEqual(pair.Value.ToString(CultureInfo.InvariantCulture), actual[pair.Key].Value);
        }
    }

    private static ObservedValue Unwrap(ObservedValue value, int wrappers)
    {
        for (var index = 0; index < wrappers; index++)
        {
            Assert.AreEqual("dictionary", value.Kind);
            Assert.EndsWith("System.Collections.Hashtable+SyncHashtable", value.Type);
            var table = Assert.ContainsSingle(value.Members);
            Assert.AreEqual("table", table.Name);
            value = table.Value;
        }

        return value;
    }

    private static Dictionary<string, ObservedValue> Entries(ObservedValue value) => value.Members
        .Where(member => member.Value.Kind == "entry").Select(member => member.Value.Members)
        .ToDictionary(pair => pair[0].Value.Value!, pair => pair[1].Value);

    private static void AssertUnavailable(ObservedValue value, string reason)
    {
        Assert.AreEqual("dictionary", value.Kind);
        Assert.HasCount(2, value.Members);
        Assert.AreEqual("remaining", value.Members[1].Name);
        Assert.AreEqual("unavailable", value.Members[1].Value.Kind);
        Assert.AreEqual(reason, value.Members[1].Value.Value);
    }

    private static ObservedValue Observe(object value) => new StructuralObservation(new Dictionary<string, string>()).Capture(value);

    private static string Details(ComparisonReply value) => value.Original.Detail + "; " + value.Edited.Detail;
}
