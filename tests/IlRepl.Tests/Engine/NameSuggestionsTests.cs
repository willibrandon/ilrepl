using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="NameSuggestions"/> and <see cref="EditDistance"/>: the did-you-mean a
/// mistyped name gets, and the bounded distance that finds it.
/// </summary>
[TestClass]
public sealed class NameSuggestionsTests
{
    /// <summary>
    /// The bounded distance agrees with the full one wherever the bound admits an answer.
    /// </summary>
    [TestMethod]
    public void WithinBound_AgreesWithLevenshtein()
    {
        var words = new[] { "Concat", "Concta", "Console", "Xonsole", "Cosnole", "WriteLine", "Write", "Trim", "Trmi", "ToString", "tostring", "", "a", "ab" };
        foreach (var a in words)
        {
            foreach (var b in words)
            {
                var distance = EditDistance.Levenshtein(a, b);
                for (var bound = 0; bound <= 3; bound++)
                {
                    Assert.AreEqual(distance <= bound, EditDistance.WithinBound(a, b, bound), $"{a} / {b} within {bound}");
                }
            }
        }
    }

    /// <summary>
    /// Only a distance under three counts, and the nearest wins.
    /// </summary>
    [TestMethod]
    public void Nearest_UsesStrictDistanceUnderThree()
    {
        var pool = new[] { "Concat", "Compare", "Contains", "Copy" };
        Assert.AreEqual("Concat", NameSuggestions.Nearest("Concta", pool));
        Assert.AreEqual("Concat", NameSuggestions.Nearest("Concat2", pool));
        Assert.IsNull(NameSuggestions.Nearest("Concatenate", pool), "three edits away is not near");
        Assert.IsNull(NameSuggestions.Nearest("Zzz", pool));
        Assert.IsNull(NameSuggestions.Nearest("Concat", pool), "the name itself is not a suggestion");
    }

    /// <summary>
    /// A name that differs only in case is the first suggestion.
    /// </summary>
    [TestMethod]
    public void Nearest_CaseOnlyDifference_Suggests()
    {
        Assert.AreEqual("ToLowerInvariant", NameSuggestions.Nearest("tolowerinvariant", ["ToLower", "ToLowerInvariant", "ToUpper"]));
        Assert.AreEqual("Add", NameSuggestions.Nearest("add", ["Sum", "Add", "Adds"]));
    }

    /// <summary>
    /// Ties keep the pool's order, so the suggestion is the same every time.
    /// </summary>
    [TestMethod]
    public void Nearest_TiesKeepPoolOrder()
    {
        Assert.AreEqual("Bat", NameSuggestions.Nearest("Bar", ["Bat", "Baz", "Bag"]));
        Assert.AreEqual("Baz", NameSuggestions.Nearest("Bar", ["Baz", "Bat", "Bag"]));
    }

    /// <summary>
    /// A wrong first letter is still found, because nothing prefilters by it.
    /// </summary>
    [TestMethod]
    public void NearestType_WrongFirstLetter_StillSuggests()
    {
        var context = new ParseContext([], [], GenericContext.Empty, new TypeResolver(), []);
        using var snapshot = BindingSnapshot.Capture(context);
        var index = new TypeIndex(snapshot);
        var scope = new SnapshotBindingScope(snapshot);
        var suggestion = NameSuggestions.NearestType("Xonsole", null, index, AccessContext.Cell, scope);
        Assert.IsNotNull(suggestion);
        Assert.AreEqual("Console", suggestion.Spelling);
        Assert.AreEqual("Console", NameSuggestions.NearestType("Cosnole", null, index, AccessContext.Cell, scope)!.Spelling);
        Assert.AreEqual("StringBuilder", NameSuggestions.NearestType("StringBuilderr", null, index, AccessContext.Cell, scope)!.Spelling);
    }

    /// <summary>
    /// The length bound prunes the pool without losing a match within it: a two-letter name allows
    /// one edit, a longer name two, and a candidate beyond the bound is never offered.
    /// </summary>
    [TestMethod]
    public void NearestType_LengthBound_PrunesWithoutLosingMatches()
    {
        var context = new ParseContext([], [], GenericContext.Empty, new TypeResolver(), []);
        using var snapshot = BindingSnapshot.Capture(context);
        var index = new TypeIndex(snapshot);
        var scope = new SnapshotBindingScope(snapshot);
        Assert.AreEqual("Math", NameSuggestions.NearestType("Mth", null, index, AccessContext.Cell, scope)!.Spelling, "one edit within a short name's bound");
        Assert.AreEqual("Console", NameSuggestions.NearestType("Consle", null, index, AccessContext.Cell, scope)!.Spelling);
        Assert.AreEqual("ConsoleKey", NameSuggestions.NearestType("Consoleeee", null, index, AccessContext.Cell, scope)!.Spelling, "two substitutions reach a neighbour");
        Assert.IsNull(NameSuggestions.NearestType("Consolezzzz", null, index, AccessContext.Cell, scope), "three edits away is beyond the bound");
        Assert.IsNull(NameSuggestions.NearestType("Qz", null, index, AccessContext.Cell, scope));
    }

    /// <summary>
    /// A type that only a derived type may name is not suggested to the cell.
    /// </summary>
    [TestMethod]
    public void NearestType_FiltersByTheContext()
    {
        var resolver = new TypeResolver();
        resolver.Load(SampleHost.Samples.GreeterDll);
        var context = new ParseContext([], [], GenericContext.Empty, resolver, []);
        using var snapshot = BindingSnapshot.Capture(context);
        var index = new TypeIndex(snapshot);
        var scope = new SnapshotBindingScope(snapshot);
        var fromCell = NameSuggestions.NearestType("Greeter.Nesting/ProtectedNestd", null, index, AccessContext.Cell, scope);
        Assert.IsNull(fromCell, "a protected nested type is not reachable from the cell");
        var publicNested = NameSuggestions.NearestType("Greeter.Nesting/PublicNestd", null, index, AccessContext.Cell, scope);
        Assert.IsNotNull(publicNested);
        Assert.AreEqual("Greeter.Nesting/PublicNested", publicNested.Entry.IlPath);
    }
}
