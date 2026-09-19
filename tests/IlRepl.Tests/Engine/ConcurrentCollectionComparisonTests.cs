using System.Collections.Concurrent;
using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Concurrent dictionary observations preserve logical contents independently of randomized storage.
/// </summary>
[TestClass]
public sealed class ConcurrentCollectionComparisonTests
{
    /// <summary>
    /// Supplies cancellation for the actual comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Independent workers match unchanged dictionaries and detect changed entries across real revisions.
    /// </summary>
    /// <param name="count">The original entry count.</param>
    /// <param name="comparer">The framework comparer setting.</param>
    [TestMethod]
    [DataRow(0, "default")]
    [DataRow(1, "default")]
    [DataRow(8, "default")]
    [DataRow(8, "Ordinal")]
    [DataRow(8, "OrdinalIgnoreCase")]
    [DataRow(8, "InvariantCultureIgnoreCase")]
    public async Task Compare_ConcurrentContentsIgnoreRandomizedStorage(int count, string comparer)
    {
        var session = IlLines.Load(ConcurrentCollectionComparisonExamples.Method(count, comparer).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        Assert.IsEmpty(edit.Problems);
        session.CommitEdit(edit.Name, ConcurrentCollectionComparisonExamples.Method(count, comparer, reverse: true));
        AssertActualContents(edit.Original.Requested.Invoke(null, null), count, false);
        AssertActualContents(edit.OriginalMethod.Invoke(null, null), count, false);
        AssertActualContents(edit.Method!.Invoke(null, null), count, false);
        var same = await CompareAsync(session);
        Assert.AreEqual("match", same.Outcome, Details(same));
        AssertContents(same.Original.Result!, count, false);
        AssertContents(same.Edited.Result!, count, false);

        session.CommitEdit(edit.Name, ConcurrentCollectionComparisonExamples.Method(count, comparer, edited: true, reverse: true));
        AssertActualContents(edit.Method!.Invoke(null, null), count, true);
        var changed = await CompareAsync(session);
        Assert.AreEqual("different", changed.Outcome, Details(changed));
        AssertContents(changed.Original.Result!, count, false);
        AssertContents(changed.Edited.Result!, count, true);
    }

    /// <summary>
    /// Comparer changes remain observable even when no dictionary entry changes.
    /// </summary>
    /// <param name="count">The empty or populated dictionary size.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(8)]
    public async Task Compare_ConcurrentComparerSettingsRemainObservable(int count)
    {
        var session = IlLines.Load(ConcurrentCollectionComparisonExamples.Method(count, "Ordinal").Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ConcurrentCollectionComparisonExamples.Method(count, "OrdinalIgnoreCase"));
        var result = await CompareAsync(session);
        Assert.AreEqual("different", result.Outcome, Details(result));
        AssertContents(result.Original.Result!, count, false);
        AssertContents(result.Edited.Result!, count, false);
        Assert.AreEqual("ordinal", result.Original.Result!.Members[0].Value.Value);
        Assert.AreEqual("ordinal-ignore-case", result.Edited.Result!.Members[0].Value.Value);
    }

    /// <summary>
    /// Insertion order, concurrency level, capacity, and removed entries do not change the logical snapshot.
    /// </summary>
    [TestMethod]
    public void Capture_ConcurrentOrderIgnoresInsertionAndCapacity()
    {
        var first = new ConcurrentDictionary<string, int>(1, 1, StringComparer.OrdinalIgnoreCase);
        var second = new ConcurrentDictionary<string, int>(4, 1000, StringComparer.OrdinalIgnoreCase);
        var entries = ConcurrentCollectionComparisonExamples.Contents(8);
        foreach (var (key, value) in entries)
        {
            Assert.IsTrue(first.TryAdd(key, value));
        }

        foreach (var (key, value) in entries.Reverse())
        {
            Assert.IsTrue(second.TryAdd(key, value));
        }

        Assert.IsTrue(second.TryAdd("removed", 99));
        Assert.IsTrue(second.TryRemove("removed", out var removed));
        Assert.AreEqual(99, removed);
        var observed = Observe(first);
        Assert.AreEqual(observed, Observe(second));
        AssertContents(observed, 8, false);
    }

    /// <summary>
    /// Comparer fields remain observable without invoking custom equality, hashing, or formatting.
    /// </summary>
    [TestMethod]
    public void Capture_ConcurrentComparerDoesNotInvokeCallbacks()
    {
        var comparer = new ImmutableObservationComparer { Salt = 17 };
        var dictionary = new ConcurrentDictionary<string, object>(comparer);
        Assert.IsTrue(dictionary.TryAdd("first", comparer));
        Assert.IsTrue(dictionary.TryAdd("second", comparer));
        Assert.IsGreaterThan(0, comparer.Callbacks);
        comparer.Callbacks = 0;
        comparer.RejectCallbacks = true;
        var observed = Observe(dictionary);
        var captured = observed.Members.Single(member => member.Name == "comparer").Value;
        Assert.AreEqual("object", captured.Kind);
        Assert.AreEqual("17", captured.Members.Single(member => member.Name.EndsWith("::Salt", StringComparison.Ordinal)).Value.Value);
        foreach (var entry in Entries(observed))
        {
            Assert.AreEqual("reference", entry.Value.Kind);
            Assert.AreEqual(captured.Identity, entry.Value.Identity);
        }

        comparer.Salt = 19;
        Assert.AreNotEqual(observed, Observe(dictionary));
        Assert.AreEqual(0, comparer.Callbacks);
    }

    /// <summary>
    /// Subclass fields retain aliases while framework capture bypasses user getters, copies, and enumerators.
    /// </summary>
    [TestMethod]
    public void Capture_ConcurrentSubclassPreservesFieldsWithoutCallbacks()
    {
        var item = new ComparisonObservedNode { Name = "stored" };
        var dictionary = new ConcurrentObservationDictionary { Extra = item };
        Assert.IsTrue(dictionary.TryAdd("first", item));
        var observed = Observe(dictionary);
        Assert.AreEqual("dictionary", observed.Kind);
        Assert.AreEqual("[IlRepl.Tests]IlRepl.Tests.Engine.ConcurrentObservationDictionary", observed.Type);
        Assert.HasCount(3, observed.Members);
        var extra = observed.Members.Single(member => member.Name.EndsWith("::Extra", StringComparison.Ordinal)).Value;
        Assert.AreEqual("object", extra.Kind);
        var entry = Assert.ContainsSingle(Entries(observed));
        Assert.AreEqual("first", entry.Key);
        Assert.AreEqual("reference", entry.Value.Kind);
        Assert.AreEqual(extra.Identity, entry.Value.Identity);
        Assert.AreEqual(0, item.UserCodeCalls);
    }

    /// <summary>
    /// Reference keys are ordered by stored structure without calling their equality, formatting, or computed members.
    /// </summary>
    [TestMethod]
    public void Capture_ConcurrentObjectKeysRemainStructural()
    {
        var first = new ComparisonObservedNode { Name = "first" };
        var second = new ComparisonObservedNode { Name = "second" };
        var dictionary = new ConcurrentDictionary<object, int>(ReferenceEqualityComparer.Instance);
        var reversed = new ConcurrentDictionary<object, int>(ReferenceEqualityComparer.Instance);
        Assert.IsTrue(dictionary.TryAdd(first, 42));
        Assert.IsTrue(dictionary.TryAdd(second, 43));
        Assert.IsTrue(reversed.TryAdd(second, 43));
        Assert.IsTrue(reversed.TryAdd(first, 42));
        var observed = Observe(dictionary);
        Assert.AreEqual(observed, Observe(reversed));
        Assert.AreEqual("dictionary", observed.Kind);
        Assert.HasCount(3, observed.Members);
        var entries = observed.Members.Skip(1).Select(member => member.Value.Members).ToDictionary(
            members => members[0].Value.Members.Single(member => member.Name.EndsWith("::Name", StringComparison.Ordinal)).Value.Value!,
            members => members[1].Value.Value);
        Assert.AreEqual("42", entries["first"]);
        Assert.AreEqual("43", entries["second"]);
        Assert.AreEqual(0, first.UserCodeCalls);
        Assert.AreEqual(0, second.UserCodeCalls);
    }

    /// <summary>
    /// Logical entries retain self cycles, shared values, null values, and aliases observed through later roots.
    /// </summary>
    [TestMethod]
    public void Capture_ConcurrentCyclesAndAliases()
    {
        var dictionary = new ConcurrentDictionary<string, object?>();
        var shared = new object();
        Assert.IsTrue(dictionary.TryAdd("self", dictionary));
        Assert.IsTrue(dictionary.TryAdd("first", shared));
        Assert.IsTrue(dictionary.TryAdd("second", shared));
        Assert.IsTrue(dictionary.TryAdd("null", null));
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var observed = observer.Capture(dictionary);
        var entries = Entries(observed);
        Assert.HasCount(4, entries);
        Assert.AreEqual("reference", entries["self"].Kind);
        Assert.AreEqual(observed.Identity, entries["self"].Identity);
        var values = new[] { entries["first"], entries["second"] };
        Assert.HasCount(1, values.Where(value => value.Kind == "object"));
        Assert.HasCount(1, values.Where(value => value.Kind == "reference"));
        Assert.AreEqual(values[0].Identity, values[1].Identity);
        Assert.AreEqual(values[0].Identity, observer.Capture(shared).Identity);
        Assert.AreEqual("null", entries["null"].Kind);
        dictionary["second"] = new object();
        Assert.AreNotEqual(observed, Observe(dictionary));
    }

    /// <summary>
    /// A singleton reference key is supported while indistinguishable distinct keys report unavailable ordering.
    /// </summary>
    [TestMethod]
    public void Capture_ConcurrentAmbiguousKeysReportUnavailable()
    {
        var dictionary = new ConcurrentDictionary<object, int>(ReferenceEqualityComparer.Instance);
        Assert.IsTrue(dictionary.TryAdd(new object(), 42));
        var singleton = Observe(dictionary);
        Assert.AreEqual("dictionary", singleton.Kind);
        Assert.HasCount(2, singleton.Members);
        Assert.AreEqual("entry", singleton.Members[1].Value.Kind);
        Assert.AreEqual("42", singleton.Members[1].Value.Members[1].Value.Value);
        Assert.IsTrue(dictionary.TryAdd(new object(), 43));
        AssertUnavailable(Observe(dictionary), "distinct collection keys have indistinguishable structural order");
    }

    /// <summary>
    /// Snapshots reject collections larger than the remaining observation budget without exposing partial storage.
    /// </summary>
    [TestMethod]
    public void Capture_ConcurrentContentsRemainBounded()
    {
        var dictionary = new ConcurrentDictionary<int, int>(Enumerable.Range(0, 5000).Select(index => KeyValuePair.Create(index, index)));
        AssertUnavailable(Observe(dictionary), "collection exceeds the observation limit");
    }

    /// <summary>
    /// Structural key ordering shares its node budget and retains every entry below that boundary.
    /// </summary>
    /// <param name="count">The number of distinct eight-element array keys.</param>
    /// <param name="limited">Whether aggregate key ordering exceeds its budget.</param>
    [TestMethod]
    [DataRow(300, false)]
    [DataRow(500, true)]
    public void Capture_ConcurrentOrderingNodesRemainBounded(int count, bool limited)
    {
        var dictionary = new ConcurrentDictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < count; index++)
        {
            Assert.IsTrue(dictionary.TryAdd(new[] { index, 0, 0, 0, 0, 0, 0, 0 }, index));
        }

        var observed = Observe(dictionary);
        if (limited)
        {
            AssertUnavailable(observed, "collection keys cannot be ordered within the observation limit");
        }
        else
        {
            Assert.HasCount(count + 1, observed.Members);
            var entries = observed.Members.Skip(1).Select(member => member.Value.Members).ToArray();
            foreach (var entry in entries)
            {
                Assert.AreEqual("array", entry[0].Value.Kind);
                Assert.HasCount(8, entry[0].Value.Members);
                Assert.AreEqual(entry[0].Value.Members[0].Value.Value, entry[1].Value.Value);
                Assert.AreSequenceEqual(Enumerable.Repeat("0", 7), entry[0].Value.Members.Skip(1).Select(member => member.Value.Value));
            }

            var expected = Enumerable.Range(0, count).Select(index => index.ToString(CultureInfo.InvariantCulture));
            Assert.AreSequenceEqual(expected.Order(StringComparer.Ordinal),
                entries.Select(entry => entry[1].Value.Value!).Order(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Key ordering bounds aggregate text even when every individual string is within its scalar limit.
    /// </summary>
    /// <param name="count">The number of distinct large string keys.</param>
    /// <param name="limited">Whether aggregate ordering exceeds its text budget.</param>
    [TestMethod]
    [DataRow(15, false)]
    [DataRow(17, true)]
    public void Capture_ConcurrentOrderingTextRemainsBounded(int count, bool limited)
    {
        var dictionary = new ConcurrentDictionary<string, int>();
        for (var index = 0; index < count; index++)
        {
            Assert.IsTrue(dictionary.TryAdd(new string((char)('a' + index), 65_500), index));
        }

        var observed = Observe(dictionary);
        if (limited)
        {
            AssertUnavailable(observed, "collection keys cannot be ordered within the observation limit");
        }
        else
        {
            Assert.HasCount(count + 1, observed.Members);
            var entries = Entries(observed);
            for (var index = 0; index < count; index++)
            {
                Assert.AreEqual(index.ToString(CultureInfo.InvariantCulture), entries[new string((char)('a' + index), 65_500)].Value);
            }
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

    private static void AssertActualContents(object? value, int count, bool edited)
    {
        var actual = Assert.IsInstanceOfType<ConcurrentDictionary<string, int>>(value);
        var expected = ConcurrentCollectionComparisonExamples.Contents(count, edited);
        Assert.HasCount(expected.Count, actual);
        foreach (var entry in expected)
        {
            Assert.AreEqual(entry.Value, actual[entry.Key]);
        }
    }

    private static void AssertContents(ObservedValue value, int count, bool edited)
    {
        Assert.AreEqual("dictionary", value.Kind);
        Assert.Contains("System.Collections.Concurrent.ConcurrentDictionary`2", value.Type);
        var expected = ConcurrentCollectionComparisonExamples.Contents(count, edited);
        Assert.HasCount(expected.Count + 1, value.Members);
        Assert.AreEqual("comparer", value.Members[0].Name);
        Assert.AreEqual("comparer", value.Members[0].Value.Kind);
        var entries = Entries(value);
        Assert.HasCount(expected.Count, entries);
        foreach (var entry in expected)
        {
            Assert.AreEqual(entry.Value.ToString(CultureInfo.InvariantCulture), entries[entry.Key].Value);
        }
    }

    private static Dictionary<string, ObservedValue> Entries(ObservedValue value) => value.Members
        .Where(member => member.Value.Kind == "entry").Select(member => member.Value.Members)
        .ToDictionary(members => members[0].Value.Value!, members => members[1].Value);

    private static void AssertUnavailable(ObservedValue value, string reason)
    {
        Assert.AreEqual("dictionary", value.Kind);
        Assert.HasCount(2, value.Members);
        Assert.AreEqual("comparer", value.Members[0].Name);
        Assert.AreEqual("remaining", value.Members[1].Name);
        Assert.AreEqual("unavailable", value.Members[1].Value.Kind);
        Assert.AreEqual(reason, value.Members[1].Value.Value);
    }

    private static ObservedValue Observe(object value) => new StructuralObservation(new Dictionary<string, string>()).Capture(value);

    private static string Details(ComparisonReply result) => result.Original.Detail + "; " + result.Edited.Detail;
}
