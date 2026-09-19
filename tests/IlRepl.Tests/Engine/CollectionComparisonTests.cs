using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Dictionary and hash-set observations preserve logical contents across independent runtimes.
/// </summary>
[TestClass]
public sealed class CollectionComparisonTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Framework comparer options produce matching contents in fresh workers while edited values remain different.
    /// </summary>
    /// <param name="set">Whether to return a hash set.</param>
    /// <param name="comparer">The framework string comparer.</param>
    [TestMethod]
    [DataRow(false, "default")]
    [DataRow(false, "Ordinal")]
    [DataRow(false, "OrdinalIgnoreCase")]
    [DataRow(false, "InvariantCulture")]
    [DataRow(false, "InvariantCultureIgnoreCase")]
    [DataRow(true, "default")]
    [DataRow(true, "Ordinal")]
    [DataRow(true, "OrdinalIgnoreCase")]
    [DataRow(true, "InvariantCulture")]
    [DataRow(true, "InvariantCultureIgnoreCase")]
    public async Task Compare_CollectionContentsIgnoreRandomizedStorage(bool set, string comparer)
    {
        var session = IlLines.Load(CollectionComparisonExamples.Method(set, comparer, false).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var same = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", same.Outcome, same.Original.Detail + "; " + same.Edited.Detail);
        foreach (var side in new[] { same.Original, same.Edited })
        {
            var value = side.Result!;
            Assert.AreEqual(set ? "set" : "dictionary", value.Kind);
            Assert.HasCount(3, value.Members);
            Assert.AreEqual("comparer", value.Members[0].Name);
            Assert.AreEqual("comparer", value.Members[0].Value.Kind);
            if (set)
            {
                Assert.AreSequenceEqual(["first", "second"], value.Members.Skip(1).Select(member => member.Value.Value));
            }
            else
            {
                Assert.AreEqual("first", value.Members[1].Value.Members[0].Value.Value);
                Assert.AreEqual("42", value.Members[1].Value.Members[1].Value.Value);
                Assert.AreEqual("second", value.Members[2].Value.Members[0].Value.Value);
                Assert.AreEqual("43", value.Members[2].Value.Members[1].Value.Value);
            }
        }

        session.CommitEdit(edit.Name, CollectionComparisonExamples.Method(set, comparer, true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        var edited = changed.Edited.Result!.Members[2].Value;
        Assert.AreEqual(set ? "changed" : "44", set ? edited.Value : edited.Members[1].Value.Value);
    }

    /// <summary>
    /// Capacity, removed storage, and cached views do not change logical contents or comparer observations.
    /// </summary>
    [TestMethod]
    public void Capture_CollectionContentsPreserveComparerOptionsAndIgnoreUnusedStorage()
    {
        var first = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["first"] = 42 };
        var second = new Dictionary<string, int>(100, StringComparer.OrdinalIgnoreCase) { ["first"] = 42, ["removed"] = 7 };
        second.Remove("removed");
        _ = second.Keys;
        _ = second.Values;
        Assert.AreEqual(Observe(first), Observe(second));
        Assert.AreNotEqual(Observe(first), Observe(new Dictionary<string, int>(StringComparer.Ordinal) { ["first"] = 42 }));
        var empty = Observe(new HashSet<string>(StringComparer.InvariantCulture));
        Assert.AreEqual("set", empty.Kind);
        Assert.HasCount(1, empty.Members);
        Assert.AreNotEqual(empty, Observe(new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)));
        Assert.AreEqual(Observe(new HashSet<string>()), Observe(new HashSet<string>(100)));
    }

    /// <summary>
    /// Cycles, shared values, and references across roots retain their identities in logical collection entries.
    /// </summary>
    [TestMethod]
    public void Capture_CollectionContentsKeepCyclesAndAliases()
    {
        var values = new Dictionary<string, object?>();
        var shared = new object();
        values.Add("self", values);
        values.Add("first", shared);
        values.Add("second", shared);
        values.Add("null", null);
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var captured = observer.Capture(values);
        var self = captured.Members[1].Value.Members[1].Value;
        var first = captured.Members[2].Value.Members[1].Value;
        var second = captured.Members[3].Value.Members[1].Value;
        Assert.AreEqual("reference", self.Kind);
        Assert.AreEqual(captured.Identity, self.Identity);
        Assert.AreEqual("object", first.Kind);
        Assert.AreEqual("reference", second.Kind);
        Assert.AreEqual(first.Identity, second.Identity);
        Assert.AreEqual(first.Identity, observer.Capture(shared).Identity);
        Assert.AreEqual("null", captured.Members[4].Value.Members[1].Value.Kind);
        var set = new HashSet<object>();
        set.Add(set);
        var cycle = Observe(set);
        Assert.AreEqual("reference", cycle.Members[1].Value.Kind);
        Assert.AreEqual(cycle.Identity, cycle.Members[1].Value.Identity);
    }

    /// <summary>
    /// Large collections stop at the existing observation budget and report the omitted contents as unavailable.
    /// </summary>
    /// <param name="set">Whether to capture a hash set.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Capture_CollectionContentsRemainBounded(bool set)
    {
        var values = Enumerable.Range(0, 5000);
        var observed = Observe(set ? (object)values.ToHashSet() : values.ToDictionary(value => value));
        Assert.AreEqual("remaining", observed.Members[^1].Name);
        Assert.AreEqual("unavailable", observed.Members[^1].Value.Kind);
        Assert.Contains("observation limit", observed.Members[^1].Value.Value!);
        Assert.IsLessThan(4097, observed.Members.Count);
    }

    /// <summary>
    /// Subclass fields retain aliases while user enumerators, getters, equality, hashes, and formatting remain uncalled.
    /// </summary>
    [TestMethod]
    public void Capture_CollectionSubclassesDoNotInvokeUserCode()
    {
        var item = new ComparisonObservedNode();
        var dictionary = new ObservationDictionary { Extra = item, ["first"] = item };
        var set = new ObservationSet { item };
        set.Extra = item;
        item.UserCodeCalls = 0;
        foreach (var source in new object[] { dictionary, set })
        {
            var value = Observe(source);
            var field = value.Members.Single(member => member.Name.EndsWith("::Extra", StringComparison.Ordinal)).Value;
            var element = value.Members.Single(member => member.Name == "0").Value;
            if (source == dictionary)
            {
                element = element.Members[1].Value;
            }

            Assert.AreEqual("object", field.Kind);
            Assert.AreEqual("reference", element.Kind);
            Assert.AreEqual(field.Identity, element.Identity);
            Assert.AreEqual(0, item.UserCodeCalls);
        }
    }

    private static ObservedValue Observe(object value) => new StructuralObservation(new Dictionary<string, string>()).Capture(value);
}
