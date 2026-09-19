using System.Collections.Immutable;
using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Immutable hash collections preserve logical contents and comparer behavior across independent runtimes.
/// </summary>
[TestClass]
public sealed class ImmutableCollectionComparisonTests
{
    private static readonly string[] ObjectKeyNames = ["first", "second"];

    /// <summary>
    /// Supplies cancellation for actual comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Empty, singleton, and larger immutable collections match across real workers and distinguish changed logical entries.
    /// </summary>
    /// <param name="set">Whether to return an immutable set.</param>
    /// <param name="count">The number of original entries.</param>
    /// <param name="keys">The key or element comparer.</param>
    /// <param name="values">The dictionary value comparer.</param>
    /// <returns>The completed original and revised worker assertions.</returns>
    [TestMethod]
    [DataRow(false, 0, "default", "default")]
    [DataRow(false, 1, "Ordinal", "default")]
    [DataRow(false, 8, "default", "default")]
    [DataRow(false, 8, "OrdinalIgnoreCase", "OrdinalIgnoreCase")]
    [DataRow(false, 8, "InvariantCultureIgnoreCase", "InvariantCulture")]
    [DataRow(true, 0, "default", "default")]
    [DataRow(true, 1, "Ordinal", "default")]
    [DataRow(true, 8, "default", "default")]
    [DataRow(true, 8, "OrdinalIgnoreCase", "default")]
    [DataRow(true, 8, "InvariantCulture", "default")]
    public async Task Compare_ImmutableContentsMatchAcrossIndependentProcesses(bool set, int count, string keys, string values)
    {
        var source = ImmutableCollectionComparisonExamples.Method(set, count, keys, values);
        var session = IlLines.Load(source.Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var same = await CompareAsync(session);
        Assert.AreEqual("match", same.Outcome, same.Original.Detail + "; " + same.Edited.Detail);
        AssertContents(same.Original.Result!, set, count, false);
        AssertContents(same.Edited.Result!, set, count, false);
        session.CommitEdit(edit.Name, ImmutableCollectionComparisonExamples.Method(set, count, keys, values, edited: true));
        var changed = await CompareAsync(session);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        AssertContents(changed.Original.Result!, set, count, false);
        AssertContents(changed.Edited.Result!, set, count, true);
    }

    /// <summary>
    /// Key and value comparer changes remain observable even when every immutable dictionary entry is unchanged.
    /// </summary>
    /// <param name="count">The entry count, including the empty boundary.</param>
    /// <param name="values">Whether to revise only the value comparer instead of only the key comparer.</param>
    /// <returns>The completed comparer-only process assertions.</returns>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(8, false)]
    [DataRow(8, true)]
    public async Task Compare_ImmutableDictionaryKeepsBothComparers(int count, bool values)
    {
        var session = IlLines.Load(ImmutableCollectionComparisonExamples.Method(false, count, "Ordinal", "Ordinal").Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ImmutableCollectionComparisonExamples.Method(false, count,
            values ? "Ordinal" : "OrdinalIgnoreCase", values ? "OrdinalIgnoreCase" : "Ordinal"));
        var result = await CompareAsync(session);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        AssertContents(result.Original.Result!, false, count, false);
        AssertContents(result.Edited.Result!, false, count, false);
        foreach (var name in new[] { "comparer", "value comparer" })
        {
            var original = Comparer(result.Original.Result!, name);
            var edited = Comparer(result.Edited.Result!, name);
            Assert.AreEqual("ordinal", original.Value);
            Assert.AreEqual(name == (values ? "value comparer" : "comparer") ? "ordinal-ignore-case" : "ordinal", edited.Value);
        }
    }

    /// <summary>
    /// Construction order does not change logical contents, including shared string identities across dictionary values.
    /// </summary>
    [TestMethod]
    public void Capture_ImmutableOrderAndPersistenceDoNotAffectContents()
    {
        var first = ImmutableDictionary<string, string>.Empty;
        var second = ImmutableDictionary<string, string>.Empty;
        var entries = ImmutableCollectionComparisonExamples.Contents(8, false, false);
        foreach (var entry in entries)
        {
            first = first.Add(entry.Key, entry.Value);
        }

        foreach (var entry in entries.Reverse())
        {
            second = second.Add(entry.Key, entry.Value);
        }

        second = second.Add("removed", "discarded").Remove("removed");
        Assert.AreEqual(Observe(first), Observe(second));
        Assert.AreEqual(Observe(entries.Keys.ToImmutableHashSet()), Observe(entries.Keys.Reverse().ToImmutableHashSet()));
        Assert.HasCount(8, first);
        Assert.HasCount(8, second);
    }

    /// <summary>
    /// Custom comparer fields and aliases remain visible without executing equality, hashing, or formatting during capture.
    /// </summary>
    [TestMethod]
    public void Capture_ImmutableComparersDoNotInvokeCallbacks()
    {
        var keys = new ImmutableObservationComparer { Salt = 17 };
        var values = new ImmutableObservationComparer { Salt = 23 };
        var dictionary = ImmutableDictionary.Create<string, string>(keys, values).Add("first", "shared").Add("second", "shared");
        var aliased = dictionary.WithComparers(keys, keys);
        var set = ImmutableHashSet.Create(keys, "first", "second");
        Assert.IsGreaterThan(0, keys.Callbacks);
        foreach (var comparer in new[] { keys, values })
        {
            comparer.Callbacks = 0;
            comparer.RejectCallbacks = true;
        }

        var observed = Observe(dictionary);
        var key = observed.Members.Single(member => member.Name == "comparer").Value;
        var value = observed.Members.Single(member => member.Name == "value comparer").Value;
        Assert.AreEqual("object", key.Kind);
        Assert.AreEqual("object", value.Kind);
        Assert.AreEqual("17", key.Members.Single(member => member.Name.EndsWith("::Salt", StringComparison.Ordinal)).Value.Value);
        Assert.AreEqual("23", value.Members.Single(member => member.Name.EndsWith("::Salt", StringComparison.Ordinal)).Value.Value);
        var shared = Observe(aliased);
        var sharedKey = shared.Members.Single(member => member.Name == "comparer").Value;
        var sharedValue = shared.Members.Single(member => member.Name == "value comparer").Value;
        Assert.AreEqual("reference", sharedValue.Kind);
        Assert.AreEqual(sharedKey.Identity, sharedValue.Identity);
        Assert.AreEqual("set", Observe(set).Kind);
        keys.Salt = 19;
        Assert.AreNotEqual(observed, Observe(dictionary));
        Assert.AreEqual(0, keys.Callbacks);
        Assert.AreEqual(0, values.Callbacks);
    }

    /// <summary>
    /// Mutable dictionary values retain self cycles, back-edges, aliases, and identities shared with later observed roots.
    /// </summary>
    [TestMethod]
    public void Capture_ImmutableDictionaryPreservesCyclesAndAliases()
    {
        var node = new ImmutableObservationLink();
        var dictionary = ImmutableDictionary<string, object?>.Empty.Add("first", node).Add("second", node).Add("null", null);
        node.Parent = dictionary;
        node.Other = node;
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var observed = observer.Capture(dictionary);
        var contents = Entries(observed).Select(entry => entry.Members[1].Value).ToArray();
        var captured = contents.Single(value => value.Kind == "object");
        var alias = contents.Single(value => value.Kind == "reference");
        Assert.AreEqual(captured.Identity, alias.Identity);
        Assert.AreEqual(captured.Identity, observer.Capture(node).Identity);
        var parent = captured.Members.Single(member => member.Name.EndsWith("::Parent", StringComparison.Ordinal)).Value;
        var self = captured.Members.Single(member => member.Name.EndsWith("::Other", StringComparison.Ordinal)).Value;
        Assert.AreEqual("reference", parent.Kind);
        Assert.AreEqual(observed.Identity, parent.Identity);
        Assert.AreEqual("reference", self.Kind);
        Assert.AreEqual(captured.Identity, self.Identity);
        Assert.HasCount(1, contents.Where(value => value.Kind == "null"));
        var first = new ImmutableObservationLink();
        var second = new ImmutableObservationLink();
        var distinct = ImmutableDictionary<string, object?>.Empty.Add("first", first).Add("second", second).Add("null", null);
        first.Parent = second.Parent = distinct;
        first.Other = first;
        second.Other = second;
        Assert.AreNotEqual(observed, Observe(distinct));
    }

    /// <summary>
    /// Large immutable collections stop at the existing observation budget and expose omitted entries explicitly.
    /// </summary>
    /// <param name="set">Whether to observe a set instead of a dictionary.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Capture_ImmutableContentsRemainBounded(bool set)
    {
        var keys = Enumerable.Range(0, 5000).Select(value => "key-" + value).ToArray();
        var observed = Observe(set ? (object)keys.ToImmutableHashSet() : keys.ToImmutableDictionary(key => key, _ => 42));
        Assert.AreEqual(set ? "set" : "dictionary", observed.Kind);
        Assert.AreEqual("remaining", observed.Members[^1].Name);
        Assert.AreEqual("unavailable", observed.Members[^1].Value.Kind);
        Assert.Contains("observation limit", observed.Members[^1].Value.Value!);
        Assert.IsLessThan(4097, observed.Members.Count);
    }

    /// <summary>
    /// Framework builders expose their current logical entries and comparer state independently of construction order.
    /// </summary>
    /// <param name="set">Whether to use an immutable set builder.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Capture_ImmutableBuildersPreserveLogicalContents(bool set)
    {
        var entries = ImmutableCollectionComparisonExamples.Contents(8, false, false);
        object first;
        object second;
        if (set)
        {
            var left = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            var right = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in entries.Keys)
            {
                left.Add(key);
            }

            foreach (var key in entries.Keys.Reverse())
            {
                right.Add(key);
            }

            first = left;
            second = right;
        }
        else
        {
            var left = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase, StringComparer.Ordinal);
            var right = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                left.Add(entry.Key, entry.Value);
            }

            foreach (var entry in entries.Reverse())
            {
                right.Add(entry.Key, entry.Value);
            }

            first = left;
            second = right;
        }

        var observed = Observe(first);
        Assert.AreEqual(observed, Observe(second));
        AssertContents(observed, set, 8, false);
        const string element = "[System.Private.CoreLib]System.String";
        var definition = "[System.Collections.Immutable]System.Collections.Immutable."
            + (set ? "ImmutableHashSet`1" : "ImmutableDictionary`2");
        var arguments = "<" + element + (set ? "" : "," + element) + ">";
        Assert.AreEqual(definition + "+Builder" + arguments, observed.Type);
        object immutable = set ? ((ImmutableHashSet<string>.Builder)first).ToImmutable()
            : ((ImmutableDictionary<string, string>.Builder)first).ToImmutable();
        var persistent = Observe(immutable);
        Assert.AreEqual(definition + arguments, persistent.Type);
        Assert.AreNotEqual(observed.Type, persistent.Type);
    }

    /// <summary>
    /// Structurally distinct object keys are ordered without invoking their equality, hashing, formatting, or computed properties.
    /// </summary>
    /// <param name="set">Whether to use the objects as set elements.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Capture_ImmutableObjectKeysDoNotInvokeUserCode(bool set)
    {
        var first = new ComparisonObservedNode { Name = "first" };
        var second = new ComparisonObservedNode { Name = "second" };
        object collection = set ? ImmutableHashSet.Create<object>(ReferenceEqualityComparer.Instance, second, first)
            : ImmutableDictionary.Create<object, int>(ReferenceEqualityComparer.Instance).Add(second, 43).Add(first, 42);
        var observed = Observe(collection);
        Assert.AreEqual(set ? "set" : "dictionary", observed.Kind);
        Assert.HasCount(set ? 3 : 4, observed.Members);
        var keys = set ? observed.Members.Where(member => member.Name != "comparer").Select(member => member.Value)
            : Entries(observed).Select(entry => entry.Members[0].Value);
        var names = keys.Select(key => key.Members.Single(member => member.Name.EndsWith("::Name", StringComparison.Ordinal)).Value.Value);
        Assert.AreSequenceEqual(ObjectKeyNames, names.Order(StringComparer.Ordinal));
        if (!set)
        {
            var entries = Entries(observed).ToDictionary(entry => entry.Members[0].Value.Members
                .Single(member => member.Name.EndsWith("::Name", StringComparison.Ordinal)).Value.Value!,
                entry => entry.Members[1].Value.Value);
            Assert.AreEqual("42", entries["first"]);
            Assert.AreEqual("43", entries["second"]);
        }

        Assert.AreEqual(0, first.UserCodeCalls);
        Assert.AreEqual(0, second.UserCodeCalls);
    }

    /// <summary>
    /// Distinct but structurally identical reference keys report unavailable ordering instead of choosing arbitrary hash order.
    /// </summary>
    /// <param name="set">Whether to use an immutable set.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Capture_ImmutableAmbiguousKeysReportUnavailable(bool set)
    {
        var first = new object();
        var second = new object();
        object singleton = set ? ImmutableHashSet.Create(ReferenceEqualityComparer.Instance, first)
            : ImmutableDictionary.Create<object, int>(ReferenceEqualityComparer.Instance).Add(first, 42);
        var single = Observe(singleton);
        Assert.AreEqual(set ? "set" : "dictionary", single.Kind);
        Assert.HasCount(set ? 2 : 3, single.Members);
        Assert.DoesNotContain(member => member.Name == "remaining", single.Members);
        object collection = set ? ImmutableHashSet.Create(ReferenceEqualityComparer.Instance, first, second)
            : ImmutableDictionary.Create<object, int>(ReferenceEqualityComparer.Instance).Add(first, 42).Add(second, 43);
        var observed = Observe(collection);
        Assert.AreEqual(set ? "set" : "dictionary", observed.Kind);
        Assert.HasCount(set ? 2 : 3, observed.Members);
        Assert.AreEqual("remaining", observed.Members[^1].Name);
        Assert.AreEqual("unavailable", observed.Members[^1].Value.Kind);
        Assert.AreEqual("distinct collection keys have indistinguishable structural order", observed.Members[^1].Value.Value);
    }

    /// <summary>
    /// Key ordering stops explicitly at its text budget even when each individual string is within the scalar limit.
    /// </summary>
    [TestMethod]
    public void Capture_ImmutableOrderingTextRemainsBounded()
    {
        var keys = Enumerable.Range(0, 17).Select(index => new string((char)('a' + index), 65_500)).ToImmutableHashSet();
        var observed = Observe(keys);
        Assert.AreEqual("set", observed.Kind);
        Assert.HasCount(2, observed.Members);
        Assert.AreEqual("remaining", observed.Members[^1].Name);
        Assert.AreEqual("unavailable", observed.Members[^1].Value.Kind);
        Assert.AreEqual("collection keys cannot be ordered within the observation limit", observed.Members[^1].Value.Value);
    }

    /// <summary>
    /// Aggregate key traversal shares its node budget while smaller collections retain every logical array key.
    /// </summary>
    /// <param name="count">The number of distinct eight-element array keys.</param>
    /// <param name="limited">Whether aggregate ordering exceeds the shared node budget.</param>
    [TestMethod]
    [DataRow(400, false)]
    [DataRow(500, true)]
    public void Capture_ImmutableOrderingNodesRemainBounded(int count, bool limited)
    {
        var keys = Enumerable.Range(0, count).Select(index => (object)new[] { index, 0, 0, 0, 0, 0, 0, 0 })
            .ToImmutableHashSet(ReferenceEqualityComparer.Instance);
        var observed = Observe(keys);
        Assert.AreEqual("set", observed.Kind);
        Assert.AreEqual("comparer", observed.Members[0].Name);
        if (limited)
        {
            Assert.HasCount(2, observed.Members);
            Assert.AreEqual("remaining", observed.Members[^1].Name);
            Assert.AreEqual("unavailable", observed.Members[^1].Value.Kind);
            Assert.AreEqual("collection keys cannot be ordered within the observation limit", observed.Members[^1].Value.Value);
        }
        else
        {
            Assert.HasCount(count + 1, observed.Members);
            foreach (var member in observed.Members.Skip(1))
            {
                Assert.AreEqual("array", member.Value.Kind);
                Assert.HasCount(8, member.Value.Members);
                Assert.AreSequenceEqual(Enumerable.Repeat("0", 7), member.Value.Members.Skip(1).Select(item => item.Value.Value));
            }

            var actual = observed.Members.Skip(1).Select(member => member.Value.Members[0].Value.Value!);
            var expected = Enumerable.Range(0, count).Select(index => index.ToString(CultureInfo.InvariantCulture));
            Assert.AreSequenceEqual(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        }
    }

    private async Task<ComparisonReply> CompareAsync(Session session)
    {
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception, side.Detail);
            Assert.IsNotNull(side.Result, side.Detail);
            Assert.HasCount(1, side.Invocations);
        }

        return result;
    }

    private static void AssertContents(ObservedValue value, bool set, int count, bool edited)
    {
        Assert.AreEqual(set ? "set" : "dictionary", value.Kind);
        var expected = ImmutableCollectionComparisonExamples.Contents(count, edited, set);
        Assert.HasCount(expected.Count + (set ? 1 : 2), value.Members);
        Assert.AreEqual("comparer", Comparer(value, "comparer").Kind);
        if (set)
        {
            var actual = value.Members.Where(member => member.Name != "comparer").Select(member => member.Value.Value!);
            Assert.AreSequenceEqual(expected.Keys.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        }
        else
        {
            Assert.AreEqual("comparer", Comparer(value, "value comparer").Kind);
            var actual = Entries(value).ToDictionary(entry => entry.Members[0].Value.Value!, entry => entry.Members[1].Value.Value);
            Assert.HasCount(expected.Count, actual);
            foreach (var (key, item) in expected)
            {
                Assert.AreEqual(item, actual[key]);
            }
        }
    }

    private static ObservedValue Comparer(ObservedValue collection, string name)
    {
        var value = collection.Members.Single(member => member.Name == name).Value;
        return value.Kind == "reference" ? collection.Members.Select(member => member.Value)
            .Single(member => member.Kind != "reference" && member.Identity == value.Identity) : value;
    }

    private static IEnumerable<ObservedValue> Entries(ObservedValue value) => value.Members.Select(member => member.Value)
        .Where(member => member.Kind == "entry");

    private static ObservedValue Observe(object value) => new StructuralObservation(new Dictionary<string, string>()).Capture(value);
}
