using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Checks accessible typo suggestions and the bounded distance used to select them.
/// </summary>
/// <remarks>
/// Tests for <see cref="NameSuggestions"/> and <see cref="EditDistance"/>: the did-you-mean a
/// mistyped name gets, and the bounded distance that finds it.
/// </remarks>
[TestClass]
public sealed class NameSuggestionsTests
{
    /// <summary>
    /// A nearer name that cannot bind the supplied parameters does not hide a valid correction.
    /// </summary>
    [TestMethod]
    public void MethodSuggestion_NearerIncompatibleOverload_OffersBindableCorrection()
    {
        var session = new Session();
        var error = Assert.ThrowsExactly<ReplException>(() =>
            MemberResolver.ResolveMethod("Math::Mxa(int32, int32)", session.State.Context, false));
        Assert.Contains("did you mean 'Max'", error.Message);
        var resolved = MemberResolver.ResolveMethod("Math::Max(int32, int32)", session.State.Context, false);
        Assert.AreEqual(typeof(Math).GetMethod(nameof(Math.Max), [typeof(int), typeof(int)]), resolved.Method);
    }

    /// <summary>
    /// The bounded distance agrees with the full one wherever the bound admits an answer.
    /// </summary>
    [TestMethod]
    public void WithinBound_AgreesWithLevenshtein()
    {
        var words = new[] { "Concat", "Concta", "Console", "Xonsole", "Cosnole", "WriteLine", "Write", "Trim", "Trmi", "ToString",
            "tostring", "", "a", "ab" };
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
    /// A typo in a qualified path suggests a type whose correction binds to the intended identity.
    /// </summary>
    /// <param name="typo">The misspelled namespace or enclosing type.</param>
    /// <param name="expected">The intended runtime type.</param>
    [TestMethod]
    [DataRow("Systm.Console", typeof(Console))]
    [DataRow("sYSTEM.Console", typeof(Console))]
    [DataRow("System.Environmnt/SpecialFolder", typeof(Environment.SpecialFolder))]
    [DataRow("Environmnt/SpecialFolder", typeof(Environment.SpecialFolder))]
    [DataRow("System.Environment/SpecialFoldr", typeof(Environment.SpecialFolder))]
    public void NearestType_QualifiedTypo_CorrectsTheWholePath(string typo, Type expected)
    {
        var session = new Session();
        using var snapshot = BindingSnapshot.Capture(session.State.Context);
        var index = new TypeIndex(snapshot);
        var scope = new SnapshotBindingScope(snapshot);
        var suggestion = NameSuggestions.NearestType(typo, null, index, AccessContext.Cell, scope);
        Assert.IsNotNull(suggestion);
        Assert.AreEqual(expected, TypeParser.Parse(suggestion.Spelling, session.State.Context));
        var error = Assert.ThrowsExactly<ReplException>(() => TypeParser.Parse(typo, session.State.Context));
        Assert.Contains(NameSuggestions.Parenthetical(suggestion.Spelling), error.Message);
        Assert.DoesNotContain("load its assembly", error.Message);
    }

    /// <summary>
    /// A matching simple name in an unrelated namespace cannot become a qualified correction.
    /// </summary>
    [TestMethod]
    public void NearestType_UnrelatedQualifier_DoesNotSuggest()
    {
        var context = new Session().State.Context;
        using var snapshot = BindingSnapshot.Capture(context);
        var index = new TypeIndex(snapshot);
        var scope = new SnapshotBindingScope(snapshot);
        Assert.IsNull(NameSuggestions.NearestType("Unrelated.Console", null, index, AccessContext.Cell, scope));
        Assert.IsNull(NameSuggestions.NearestType("System.Console", null, index, AccessContext.Cell, scope));
    }

    /// <summary>
    /// The edit-distance length bound prunes only names that cannot meet the suggestion threshold.
    /// </summary>
    [TestMethod]
    public void NearestType_LengthBound_PrunesWithoutLosingMatches()
    {
        var context = new ParseContext([], [], GenericContext.Empty, new TypeResolver(), []);
        using var snapshot = BindingSnapshot.Capture(context);
        var index = new TypeIndex(snapshot);
        var scope = new SnapshotBindingScope(snapshot);
        Assert.AreEqual("Math", NameSuggestions.NearestType("Mth", null, index, AccessContext.Cell, scope)!.Spelling,
            "one edit within a short name's bound");
        Assert.AreEqual("Console", NameSuggestions.NearestType("Consle", null, index, AccessContext.Cell, scope)!.Spelling);
        Assert.AreEqual("ConsoleKey", NameSuggestions.NearestType("Consoleeee", null, index, AccessContext.Cell, scope)!.Spelling,
            "two substitutions reach a neighbour");
        Assert.IsNull(NameSuggestions.NearestType("Consolezzzz", null, index, AccessContext.Cell, scope),
            "three edits away is beyond the bound");
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
