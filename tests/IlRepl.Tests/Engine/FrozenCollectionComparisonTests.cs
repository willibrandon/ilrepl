using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Frozen collections retain logical contents across optimized implementations and independent runtimes.
/// </summary>
[TestClass]
public sealed class FrozenCollectionComparisonTests
{
    private const string AssemblyPrefix = "[System.Collections.Immutable]System.Collections.Frozen.";
    private const string StringType = "[System.Private.CoreLib]System.String";
    private const string IntType = "[System.Private.CoreLib]System.Int32";

    /// <summary>
    /// Supplies cancellation for actual comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Empty, singleton and populated frozen collections match across workers and distinguish edited logical contents.
    /// </summary>
    /// <param name="set">Whether to return a set.</param>
    /// <param name="count">The original entry count.</param>
    /// <param name="comparer">The factory comparer setting.</param>
    [TestMethod]
    [DataRow(false, 0, "default")]
    [DataRow(false, 1, "Ordinal")]
    [DataRow(false, 8, "default")]
    [DataRow(false, 8, "OrdinalIgnoreCase")]
    [DataRow(false, 8, "InvariantCultureIgnoreCase")]
    [DataRow(true, 0, "default")]
    [DataRow(true, 1, "Ordinal")]
    [DataRow(true, 8, "default")]
    [DataRow(true, 8, "OrdinalIgnoreCase")]
    [DataRow(true, 8, "InvariantCultureIgnoreCase")]
    public async Task Compare_FrozenContentsMatchAcrossIndependentProcesses(bool set, int count, string comparer)
    {
        var session = IlLines.Load(FrozenCollectionComparisonExamples.Method(set, count, comparer).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        Assert.IsEmpty(edit.Problems);
        session.CommitEdit(edit.Name, FrozenCollectionComparisonExamples.Method(set, count, comparer, reverse: true));
        foreach (var method in new[] { edit.Original.Requested, edit.OriginalMethod, edit.Method! })
        {
            AssertActual(method.Invoke(null, null), set, count, false);
        }

        var same = await CompareAsync(session);
        Assert.AreEqual("match", same.Outcome, Details(same));
        AssertContents(same.Original.Result!, set, count, false);
        AssertContents(same.Edited.Result!, set, count, false);
        session.CommitEdit(edit.Name, FrozenCollectionComparisonExamples.Method(set, count, comparer, edited: true));
        AssertActual(edit.Method!.Invoke(null, null), set, count, true);
        AssertActual(edit.Original.Requested.Invoke(null, null), set, count, false);
        var changed = await CompareAsync(session);
        Assert.AreEqual("different", changed.Outcome, Details(changed));
        AssertContents(changed.Original.Result!, set, count, false);
        AssertContents(changed.Edited.Result!, set, count, true);
    }

    /// <summary>
    /// Comparer-only edits are visible even when the collection is empty or all stored elements remain unchanged.
    /// </summary>
    /// <param name="set">Whether to compare a set.</param>
    /// <param name="count">The empty or populated entry count.</param>
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 8)]
    [DataRow(true, 0)]
    [DataRow(true, 8)]
    public async Task Compare_FrozenComparerSettingsRemainObservable(bool set, int count)
    {
        var session = IlLines.Load(FrozenCollectionComparisonExamples.Method(set, count, "Ordinal").Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, FrozenCollectionComparisonExamples.Method(set, count, "OrdinalIgnoreCase"));
        var result = await CompareAsync(session);
        Assert.AreEqual("different", result.Outcome, Details(result));
        AssertContents(result.Original.Result!, set, count, false);
        AssertContents(result.Edited.Result!, set, count, false);
        Assert.AreEqual("ordinal", result.Original.Result!.Members[0].Value.Value);
        Assert.AreEqual("ordinal-ignore-case", result.Edited.Result!.Members[0].Value.Value);
    }

    /// <summary>
    /// Factory-selected empty, small, numeric and string implementations expose exact logical contents and normalized base types.
    /// </summary>
    /// <param name="set">Whether to construct frozen sets.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Capture_FrozenOptimizationVariantsPreserveLogicalContents(bool set)
    {
        var implementations = new HashSet<Type>();
        foreach (var count in new[] { 0, 1, 64 })
        {
            var numbers = Enumerable.Range(0, count).Select(index => index * 17).ToArray();
            object first = set ? numbers.ToFrozenSet() : numbers.ToFrozenDictionary(number => number, number => number + 42);
            object reversed = set ? numbers.Reverse().ToFrozenSet()
                : numbers.Reverse().ToFrozenDictionary(number => number, number => number + 42);
            implementations.Add(first.GetType());
            var observed = Observe(first);
            Assert.AreEqual(observed, Observe(reversed));
            Assert.AreEqual(set ? "set" : "dictionary", observed.Kind);
            Assert.AreEqual(AssemblyPrefix + (set ? "FrozenSet`1<" + IntType + ">"
                : "FrozenDictionary`2<" + IntType + "," + IntType + ">"), observed.Type);
            Assert.HasCount(count + 1, observed.Members);
            if (set)
            {
                var actual = observed.Members.Skip(1).Select(member => int.Parse(member.Value.Value!, CultureInfo.InvariantCulture));
                Assert.AreSequenceEqual(numbers.Order(), actual.Order());
                if (count == 64)
                {
                    Assert.IsFalse(first.GetType().IsGenericType);
                }
            }
            else
            {
                var actual = Entries(observed);
                foreach (var number in numbers)
                {
                    Assert.AreEqual((number + 42).ToString(CultureInfo.InvariantCulture),
                        actual[number.ToString(CultureInfo.InvariantCulture)].Value);
                }
            }
        }

        Assert.HasCount(3, implementations);
        var strings = FrozenCollectionComparisonExamples.Contents(set, 8);
        object words = set ? strings.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            : strings.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        object reversedWords = set ? strings.Keys.Reverse().ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            : strings.Reverse().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(Observe(words), Observe(reversedWords));
        AssertContents(Observe(words), set, 8, false);
        Assert.IsTrue(words.GetType().IsSubclassOf(set ? typeof(FrozenSet<string>) : typeof(FrozenDictionary<string, int>)));
        foreach (var baseType in new[] { typeof(FrozenSet<string>), typeof(FrozenDictionary<string, int>) })
        {
            var constructor = Assert.ContainsSingle(baseType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.IsTrue(constructor.IsFamilyAndAssembly);
        }
    }

    /// <summary>
    /// Actual custom comparer fields and aliases survive without hashing, equality or formatting callbacks during capture.
    /// </summary>
    [TestMethod]
    public void Capture_FrozenComparerDoesNotInvokeCallbacks()
    {
        var comparer = new ImmutableObservationComparer { Salt = 17 };
        var entries = new Dictionary<string, object> { ["first"] = comparer, ["second"] = comparer };
        var dictionary = entries.ToFrozenDictionary(comparer);
        var set = entries.Keys.ToFrozenSet(comparer);
        Assert.IsGreaterThan(0, comparer.Callbacks);
        comparer.Callbacks = 0;
        comparer.RejectCallbacks = true;
        var observed = Observe(dictionary);
        var captured = observed.Members[0].Value;
        Assert.AreEqual("object", captured.Kind);
        Assert.AreEqual("17", captured.Members.Single(member => member.Name.EndsWith("::Salt", StringComparison.Ordinal)).Value.Value);
        foreach (var value in Entries(observed).Values)
        {
            Assert.AreEqual("reference", value.Kind);
            Assert.AreEqual(captured.Identity, value.Identity);
        }

        var observedSet = Observe(set);
        Assert.AreEqual("set", observedSet.Kind);
        Assert.HasCount(3, observedSet.Members);
        Assert.AreSequenceEqual(entries.Keys.Order(StringComparer.Ordinal),
            observedSet.Members.Skip(1).Select(member => member.Value.Value).Order(StringComparer.Ordinal));
        comparer.Salt = 19;
        Assert.AreNotEqual(observed, Observe(dictionary));
        Assert.AreNotEqual(observedSet, Observe(set));
        Assert.AreEqual(0, comparer.Callbacks);
    }

    /// <summary>
    /// Frozen roots retain normalized repeated-reference types, shared values, null values and mutable back-edges.
    /// </summary>
    [TestMethod]
    public void Capture_FrozenCyclesAndAliasesRetainNormalizedReferenceTypes()
    {
        var shared = new ImmutableObservationLink();
        var dictionary = new Dictionary<string, object?> { ["first"] = shared, ["second"] = shared, ["null"] = null }
            .ToFrozenDictionary();
        shared.Parent = dictionary;
        shared.Other = shared;
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var observed = observer.Capture(dictionary);
        var entries = Entries(observed);
        var node = Assert.ContainsSingle(entries.Values.Where(value => value.Kind == "object"));
        var alias = Assert.ContainsSingle(entries.Values.Where(value => value.Kind == "reference"));
        Assert.AreEqual(node.Identity, alias.Identity);
        var parent = node.Members.Single(member => member.Name.EndsWith("::Parent", StringComparison.Ordinal)).Value;
        var self = node.Members.Single(member => member.Name.EndsWith("::Other", StringComparison.Ordinal)).Value;
        Assert.AreEqual("reference", parent.Kind);
        Assert.AreEqual(observed.Identity, parent.Identity);
        Assert.AreEqual(observed.Type, parent.Type);
        Assert.AreEqual("reference", self.Kind);
        Assert.AreEqual(node.Identity, self.Identity);
        Assert.AreEqual("null", entries["null"].Kind);
        Assert.AreEqual(node.Identity, observer.Capture(shared).Identity);
        var repeated = observer.Capture(dictionary);
        Assert.AreEqual("reference", repeated.Kind);
        Assert.AreEqual(observed.Identity, repeated.Identity);
        Assert.AreEqual(observed.Type, repeated.Type);
        var distinct = new ImmutableObservationLink();
        var changed = new Dictionary<string, object?> { ["first"] = shared, ["second"] = distinct, ["null"] = null }
            .ToFrozenDictionary();
        shared.Parent = distinct.Parent = changed;
        distinct.Other = distinct;
        Assert.AreNotEqual(observed, Observe(changed));
    }

    /// <summary>
    /// Frozen sets preserve null elements and stable reference identity through subsequent root observations.
    /// </summary>
    [TestMethod]
    public void Capture_FrozenSetRetainsNullAndRepeatedReferences()
    {
        string?[] items = ["first", null, "second"];
        var set = items.ToFrozenSet();
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var observed = observer.Capture(set);
        Assert.AreEqual("set", observed.Kind);
        Assert.HasCount(4, observed.Members);
        Assert.ContainsSingle(observed.Members.Skip(1).Where(member => member.Value.Kind == "null"));
        Assert.AreSequenceEqual(items.Order(StringComparer.Ordinal),
            observed.Members.Skip(1).Select(member => member.Value.Value).Order(StringComparer.Ordinal));
        var repeated = observer.Capture(set);
        Assert.AreEqual("reference", repeated.Kind);
        Assert.AreEqual(observed.Identity, repeated.Identity);
        Assert.AreEqual(observed.Type, repeated.Type);
    }

    /// <summary>
    /// Distinct structural object keys need no user callbacks; indistinguishable reference keys are explicitly unavailable.
    /// </summary>
    /// <param name="set">Whether to observe set elements instead of dictionary keys.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Capture_FrozenObjectKeysRemainStructuralAndBounded(bool set)
    {
        var first = new ComparisonObservedNode { Name = "first" };
        var second = new ComparisonObservedNode { Name = "second" };
        var entries = new Dictionary<object, int>(ReferenceEqualityComparer.Instance) { [first] = 42, [second] = 43 };
        object collection = set ? entries.Keys.ToFrozenSet(ReferenceEqualityComparer.Instance)
            : entries.ToFrozenDictionary(ReferenceEqualityComparer.Instance);
        object reversed = set ? entries.Keys.Reverse().ToFrozenSet(ReferenceEqualityComparer.Instance)
            : entries.Reverse().ToFrozenDictionary(ReferenceEqualityComparer.Instance);
        var observed = Observe(collection);
        Assert.AreEqual(observed, Observe(reversed));
        Assert.AreEqual(set ? "set" : "dictionary", observed.Kind);
        Assert.HasCount(3, observed.Members);
        var actual = observed.Members.Skip(1).ToDictionary(
            member => (set ? member.Value : member.Value.Members[0].Value).Members
                .Single(field => field.Name.EndsWith("::Name", StringComparison.Ordinal)).Value.Value!,
            member => set ? "element" : member.Value.Members[1].Value.Value);
        Assert.AreEqual(set ? "element" : "42", actual["first"]);
        Assert.AreEqual(set ? "element" : "43", actual["second"]);
        Assert.AreEqual(0, first.UserCodeCalls);
        Assert.AreEqual(0, second.UserCodeCalls);
        entries = new Dictionary<object, int>(ReferenceEqualityComparer.Instance) { [new object()] = 42 };
        var singleton = Observe(set ? (object)entries.Keys.ToFrozenSet(ReferenceEqualityComparer.Instance)
            : entries.ToFrozenDictionary(ReferenceEqualityComparer.Instance));
        Assert.HasCount(2, singleton.Members);
        Assert.AreEqual(set ? "object" : "entry", singleton.Members[1].Value.Kind);
        if (!set)
        {
            Assert.AreEqual("42", singleton.Members[1].Value.Members[1].Value.Value);
        }

        entries.Add(new object(), 43);
        AssertUnavailable(Observe(set ? (object)entries.Keys.ToFrozenSet(ReferenceEqualityComparer.Instance)
            : entries.ToFrozenDictionary(ReferenceEqualityComparer.Instance)),
            "distinct collection keys have indistinguishable structural order");
    }

    /// <summary>
    /// Smaller collections remain complete while output and aggregate key-ordering budgets have precise unavailable results.
    /// </summary>
    /// <param name="set">Whether to observe a set.</param>
    /// <param name="kind">The entry, node or text budget partition.</param>
    /// <param name="count">The number of real keys.</param>
    /// <param name="limited">Whether the selected budget is exceeded.</param>
    [TestMethod]
    [DataRow(false, "entries", 1000, false)]
    [DataRow(false, "entries", 5000, true)]
    [DataRow(true, "entries", 1000, false)]
    [DataRow(true, "entries", 5000, true)]
    [DataRow(false, "nodes", 300, false)]
    [DataRow(false, "nodes", 500, true)]
    [DataRow(true, "text", 15, false)]
    [DataRow(true, "text", 17, true)]
    public void Capture_FrozenBudgetsRemainExplicit(bool set, string kind, int count, bool limited)
    {
        var entries = new Dictionary<object, int>();
        for (var index = 0; index < count; index++)
        {
            object key = kind switch
            {
                "nodes" => Enumerable.Repeat(index, 8).ToArray(),
                "text" => new string((char)('a' + index), 65500),
                _ => index,
            };
            entries.Add(key, index);
        }

        var observed = Observe(set ? (object)entries.Keys.ToFrozenSet() : entries.ToFrozenDictionary());
        if (limited)
        {
            AssertUnavailable(observed, kind == "entries" ? "collection exceeds the observation limit"
                : "collection keys cannot be ordered within the observation limit");
            return;
        }

        Assert.AreEqual(set ? "set" : "dictionary", observed.Kind);
        Assert.HasCount(count + 1, observed.Members);
        var numbers = new List<int>();
        foreach (var member in observed.Members.Skip(1))
        {
            var key = set ? member.Value : member.Value.Members[0].Value;
            var number = kind switch
            {
                "nodes" => int.Parse(key.Members[0].Value.Value!, CultureInfo.InvariantCulture),
                "text" => key.Value![0] - 'a',
                _ => int.Parse(key.Value!, CultureInfo.InvariantCulture),
            };
            numbers.Add(number);
            if (!set)
            {
                Assert.AreEqual(number.ToString(CultureInfo.InvariantCulture), member.Value.Members[1].Value.Value);
            }

            if (kind == "nodes")
            {
                Assert.HasCount(8, key.Members);
                foreach (var item in key.Members)
                {
                    Assert.AreEqual(number.ToString(CultureInfo.InvariantCulture), item.Value.Value);
                }
            }

            if (kind == "text")
            {
                Assert.AreEqual(new string((char)('a' + number), 65500), key.Value);
            }
        }

        Assert.AreSequenceEqual(Enumerable.Range(0, count), numbers.Order());
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

    private static void AssertActual(object? value, bool set, int count, bool edited)
    {
        var entries = FrozenCollectionComparisonExamples.Contents(set, count, edited);
        if (set)
        {
            var collection = Assert.IsInstanceOfType<FrozenSet<string>>(value);
            Assert.HasCount(entries.Count, collection);
            Assert.AreSequenceEqual(entries.Keys.Order(StringComparer.Ordinal), collection.Order(StringComparer.Ordinal));
        }
        else
        {
            var collection = Assert.IsInstanceOfType<FrozenDictionary<string, int>>(value);
            Assert.HasCount(entries.Count, collection);
            foreach (var pair in entries)
            {
                Assert.AreEqual(pair.Value, collection[pair.Key]);
            }
        }
    }

    private static void AssertContents(ObservedValue value, bool set, int count, bool edited)
    {
        Assert.AreEqual(set ? "set" : "dictionary", value.Kind);
        Assert.AreEqual(AssemblyPrefix + (set ? "FrozenSet`1<" + StringType + ">"
            : "FrozenDictionary`2<" + StringType + "," + IntType + ">"), value.Type);
        var entries = FrozenCollectionComparisonExamples.Contents(set, count, edited);
        Assert.HasCount(entries.Count + 1, value.Members);
        Assert.AreEqual("comparer", value.Members[0].Name);
        Assert.AreEqual("comparer", value.Members[0].Value.Kind);
        if (set)
        {
            Assert.AreSequenceEqual(entries.Keys.Order(StringComparer.Ordinal),
                value.Members.Skip(1).Select(member => member.Value.Value).Order(StringComparer.Ordinal));
        }
        else
        {
            var actual = Entries(value);
            foreach (var pair in entries)
            {
                Assert.AreEqual(pair.Value.ToString(CultureInfo.InvariantCulture), actual[pair.Key].Value);
            }
        }
    }

    private static Dictionary<string, ObservedValue> Entries(ObservedValue value) => value.Members.Skip(1)
        .ToDictionary(member => member.Value.Members[0].Value.Value!, member => member.Value.Members[1].Value);

    private static void AssertUnavailable(ObservedValue value, string reason)
    {
        Assert.HasCount(2, value.Members);
        Assert.AreEqual("remaining", value.Members[1].Name);
        Assert.AreEqual("unavailable", value.Members[1].Value.Kind);
        Assert.AreEqual(reason, value.Members[1].Value.Value);
    }

    private static ObservedValue Observe(object value) => new StructuralObservation(new Dictionary<string, string>()).Capture(value);

    private static string Details(ComparisonReply result) => result.Original.Detail + "; " + result.Edited.Detail;
}
