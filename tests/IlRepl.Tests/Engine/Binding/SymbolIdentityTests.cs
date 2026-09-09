using System.Reflection;
using System.Runtime.Loader;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Verifies definition, construction, parameter, and member identities across runtime imports.
/// </summary>
/// <remarks>
/// Tests for <see cref="SymbolIdentity"/> and the identities <see cref="RuntimeSymbolImporter"/>
/// gives runtime objects: a definition is one identity per load, a construction is its definition
/// and its arguments, and a generic parameter is its owner and position.
/// </remarks>
[TestClass]
public sealed partial class SymbolIdentityTests
{
    /// <summary>
    /// A primitive spelled by keyword and the CoreLib type behind it are the same symbol.
    /// </summary>
    [TestMethod]
    public void Primitive_KeywordAndRuntimeType_AreOneSymbol()
    {
        var keyword = TypeSymbol.Primitive("int32");
        var imported = RuntimeSymbolImporter.Import(typeof(int));
        Assert.AreEqual(keyword, imported);
        Assert.AreEqual(keyword.GetHashCode(), imported.GetHashCode());
        Assert.AreNotEqual(keyword, TypeSymbol.Primitive("uint32"));
        Assert.AreEqual(TypeSymbolKind.Named, RuntimeSymbolImporter.Import(typeof(decimal)).Kind);
    }

    /// <summary>
    /// A definition imported twice is one identity; its constructions differ by argument.
    /// </summary>
    [TestMethod]
    public void Definition_ImportedTwice_IsOneIdentity()
    {
        var first = RuntimeSymbolImporter.Import(typeof(List<>));
        var second = RuntimeSymbolImporter.Import(typeof(List<>));
        Assert.AreEqual(first, second);
        Assert.AreEqual(first.Definition, second.Definition);
        Assert.AreEqual("List`1", first.Name);
        Assert.AreSequenceEqual(["T"], first.GenericParameterNames.ToArray());

        var ofInt = RuntimeSymbolImporter.Import(typeof(List<int>));
        var ofString = RuntimeSymbolImporter.Import(typeof(List<string>));
        Assert.AreEqual(TypeSymbolKind.Constructed, ofInt.Kind);
        Assert.AreEqual(first, ofInt.Element);
        Assert.AreNotEqual(ofInt, ofString);
        Assert.AreEqual(ofInt, TypeSymbol.Construct(first, [TypeSymbol.Primitive("int32")]));
    }

    /// <summary>
    /// Array kinds, byrefs, and pointers over the same element are distinct symbols.
    /// </summary>
    [TestMethod]
    public void Shapes_OverTheSameElement_AreDistinct()
    {
        var vector = RuntimeSymbolImporter.Import(typeof(int[]));
        var rankOne = RuntimeSymbolImporter.Import(typeof(int).MakeArrayType(1));
        var matrix = RuntimeSymbolImporter.Import(typeof(int[,]));
        var byRef = RuntimeSymbolImporter.Import(typeof(int).MakeByRefType());
        var pointer = RuntimeSymbolImporter.Import(typeof(int*));
        var all = new[] { vector, rankOne, matrix, byRef, pointer };
        for (var i = 0; i < all.Length; i++)
        {
            for (var j = 0; j < all.Length; j++)
            {
                Assert.AreEqual(i == j, SymbolIdentity.Equal(all[i], all[j]), $"{all[i]} vs {all[j]}");
            }
        }

        Assert.AreEqual(vector, TypeSymbol.SzArray(TypeSymbol.Primitive("int32")));
    }

    /// <summary>
    /// Distinguishes generic parameters by owner, kind, and position.
    /// </summary>
    /// <remarks>
    /// A generic parameter is its owner and position: <c>!0</c> is never <c>!!0</c>, and the T of
    /// one type is never the T of another.
    /// </remarks>
    [TestMethod]
    public void GenericParameters_AreOwnerAndPosition()
    {
        var listT = RuntimeSymbolImporter.Import(typeof(List<>).GetGenericArguments()[0]);
        var listTAgain = RuntimeSymbolImporter.Import(typeof(List<>).GetGenericArguments()[0]);
        var dictionaryK = RuntimeSymbolImporter.Import(typeof(Dictionary<,>).GetGenericArguments()[0]);
        var dictionaryV = RuntimeSymbolImporter.Import(typeof(Dictionary<,>).GetGenericArguments()[1]);
        var selectT = RuntimeSymbolImporter.Import(typeof(Enumerable).GetMethod("Range")!.DeclaringType!.GetMethods().First(m
            => m.Name == "Select" && m.GetGenericArguments().Length == 2).GetGenericArguments()[0]);
        Assert.AreEqual(listT, listTAgain);
        Assert.AreNotEqual(listT, dictionaryK);
        Assert.AreNotEqual(dictionaryK, dictionaryV);
        Assert.AreEqual(TypeSymbolKind.TypeParameter, listT.Kind);
        Assert.AreEqual(TypeSymbolKind.MethodParameter, selectT.Kind);
        Assert.AreNotEqual(listT, selectT);
        Assert.AreEqual("!T", SymbolRenderer.Pretty(listT));
        Assert.AreEqual("!!TSource", SymbolRenderer.Pretty(selectT));
    }

    /// <summary>
    /// A recursive constraint, <c>T : IComparable&lt;T&gt;</c>, imports without looping.
    /// </summary>
    [TestMethod]
    public void GenericParameter_RecursiveConstraint_Imports()
    {
        var parameter = typeof(Recursive<>).GetGenericArguments()[0];
        var symbol = RuntimeSymbolImporter.ImportParameter(parameter);
        Assert.HasCount(1, symbol.Constraints);
        Assert.AreEqual(TypeSymbolKind.Constructed, symbol.Constraints[0].Kind);
        Assert.AreEqual(symbol.AsType, symbol.Constraints[0].Arguments[0]);
    }

    /// <summary>
    /// Distinguishes constructed owners and method arguments even when their metadata tokens match.
    /// </summary>
    [TestMethod]
    public void Members_OnDifferentConstructions_AreDistinct()
    {
        var addInt = RuntimeSymbolImporter.Import(typeof(List<int>).GetMethod("Add")!);
        var addString = RuntimeSymbolImporter.Import(typeof(List<string>).GetMethod("Add")!);
        var addIntAgain = RuntimeSymbolImporter.Import(typeof(List<int>).GetMethod("Add")!);
        Assert.AreEqual(addInt.Definition, addString.Definition);
        Assert.AreNotEqual(addInt, addString);
        Assert.AreEqual(addInt, addIntAgain);
        Assert.AreEqual(TypeSymbol.Primitive("int32"), addInt.Parameters[0].Type);
        Assert.AreEqual(TypeSymbol.Primitive("string"), addString.Parameters[0].Type);

        var emptyDefinition = typeof(Enumerable).GetMethod("Empty")!;
        var emptyInt = RuntimeSymbolImporter.Import(emptyDefinition.MakeGenericMethod(typeof(int)));
        var emptyString = RuntimeSymbolImporter.Import(emptyDefinition.MakeGenericMethod(typeof(string)));
        Assert.AreEqual(emptyInt.Definition, emptyString.Definition);
        Assert.AreNotEqual(emptyInt, emptyString);
        Assert.AreEqual(RuntimeSymbolImporter.Import(emptyDefinition).Definition, emptyInt.Definition);
        Assert.IsTrue(RuntimeSymbolImporter.Import(emptyDefinition).IsGenericDefinition);
        Assert.IsFalse(emptyInt.IsGenericDefinition);

        var countInt = RuntimeSymbolImporter.Import(typeof(List<int>).GetField("_size", BindingFlags.NonPublic | BindingFlags.Instance)!);
        var countString = RuntimeSymbolImporter.Import(typeof(List<string>).GetField("_size", BindingFlags.NonPublic
            | BindingFlags.Instance)!);
        Assert.AreEqual(countInt.Definition, countString.Definition);
        Assert.AreNotEqual(countInt, countString);
    }

    /// <summary>
    /// The same bytes loaded into two contexts are two identities, whatever their tokens and module ids say.
    /// </summary>
    [TestMethod]
    public void Definition_LoadedTwice_IsTwoIdentities()
    {
        var (_, image, _) = CecilFixture.Build((_, type) => { });
        var first = new AssemblyLoadContext("identity-first", isCollectible: true);
        var second = new AssemblyLoadContext("identity-second", isCollectible: true);
        try
        {
            var a = first.LoadFromStream(new MemoryStream(image)).GetTypes().Single(t => t.Name == "Fixture");
            var b = second.LoadFromStream(new MemoryStream(image)).GetTypes().Single(t => t.Name == "Fixture");
            var symbolA = RuntimeSymbolImporter.Import(a);
            var symbolB = RuntimeSymbolImporter.Import(b);
            Assert.AreEqual(a.MetadataToken, b.MetadataToken);
            Assert.AreEqual(a.Module.ModuleVersionId, b.Module.ModuleVersionId);
            Assert.AreEqual(symbolA.Definition.Token, symbolB.Definition.Token);
            Assert.AreNotEqual(symbolA, symbolB);
            Assert.AreNotEqual(symbolA.Definition.Assembly, symbolB.Definition.Assembly);
            Assert.AreSame(a, RuntimeDefinitions.TypeOf(symbolA.Definition));
            Assert.AreSame(b, RuntimeDefinitions.TypeOf(symbolB.Definition));
        }
        finally
        {
            first.Unload();
            second.Unload();
        }
    }

    /// <summary>
    /// A symbol renders as the runtime type it was imported from renders.
    /// </summary>
    /// <param name="type">The type.</param>
    [TestMethod]
    [DataRow(typeof(int))]
    [DataRow(typeof(string[]))]
    [DataRow(typeof(int[,]))]
    [DataRow(typeof(Dictionary<string, List<int>>))]
    [DataRow(typeof(List<>))]
    [DataRow(typeof(KeyValuePair<,>))]
    [DataRow(typeof(Environment.SpecialFolder))]
    [DataRow(typeof(nint))]
    [DataRow(typeof(void))]
    public void Pretty_MatchesTheTypeNameFormatter(Type type)
    {
        Assert.AreEqual(IlRepl.Engine.TypeNameFormatter.Pretty(type), SymbolRenderer.Pretty(RuntimeSymbolImporter.Import(type)));
        Assert.AreEqual(IlRepl.Engine.TypeNameFormatter.Pretty(type.MakeByRefType()), SymbolRenderer.Pretty(RuntimeSymbolImporter.Import(
            type.MakeByRefType())));
    }

    /// <summary>
    /// A loaded method renders as the resolver describes it.
    /// </summary>
    [TestMethod]
    public void Describe_MatchesTheResolver()
    {
        foreach (var method in new MethodBase[]
        {
            typeof(Console).GetMethod("WriteLine", [typeof(string)])!,
            typeof(string).GetMethod("Trim", Type.EmptyTypes)!,
            typeof(System.Text.StringBuilder).GetConstructor([typeof(int)])!,
            typeof(Enumerable).GetMethod("Empty")!,
            typeof(Enumerable).GetMethod("Empty")!.MakeGenericMethod(typeof(int)),
            typeof(List<int>).GetMethod("Add")!,
        })
        {
            Assert.AreEqual(IlRepl.Engine.MemberResolver.Describe(method), SymbolRenderer.Describe(RuntimeSymbolImporter.Import(method)));
        }
    }
}
