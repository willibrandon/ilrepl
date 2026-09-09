using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// General array signatures retain the dimensions encoded by the native IL assembler.
/// </summary>
[TestClass]
public sealed class ArraySignatureTests
{
    /// <summary>
    /// Parsing and completion agree with ILAsm on explicit bounds, sizes, and omitted dimension prefixes.
    /// </summary>
    /// <param name="shape">The ILAsm array dimensions.</param>
    [TestMethod]
    [DataRow("...")]
    [DataRow(",")]
    [DataRow("1...")]
    [DataRow("3")]
    [DataRow("1...4")]
    [DataRow("-2...1,5...")]
    [DataRow(",2...4")]
    [DataRow("1...,2...4")]
    public void BindArray_AgreesWithIlasm(string shape)
    {
        var name = "ArrayShapes" + Guid.NewGuid().ToString("N");
        var image = IlasmLocator.Assemble($$"""
            .assembly extern System.Runtime {}
            .assembly {{name}} {}
            .module {{name}}.dll
            .class public {{name}} extends [System.Runtime]System.Object {
                .field public static int32[{{shape}}] Data
            }
            """);
        var session = new Session();
        var assembly = session.State.Resolver.LoadImage(image);
        using var captured = BindingSnapshot.Capture(session.State.Context);
        var scope = new SnapshotBindingScope(captured);
        var reference = CilSyntaxParser.ParseFieldReference(name + "::Data");
        var expected = SymbolBinder.BindFieldReference(reference, scope).FieldType;
        var bound = SymbolBinder.BindType(CilSyntaxParser.ParseType("int32[" + shape + "]"), scope).Type;
        Assert.AreEqual(expected, bound);
        Assert.AreEqual(expected.GetHashCode(), bound.GetHashCode());
        Assert.AreEqual(expected, RuntimeSymbolImporter.Import(assembly.GetType(name)!.GetField("Data")!).FieldType);
        var spelling = new TypeSpeller(scope).Spell(expected);
        Assert.AreEqual(expected, SymbolBinder.BindType(CilSyntaxParser.ParseType(spelling), scope).Type);
    }

    /// <summary>
    /// Size-only dimensions round-trip without adding a lower-bound entry to their signature.
    /// </summary>
    /// <param name="shape">The exact REPL array dimensions.</param>
    [TestMethod]
    [DataRow("...+3")]
    [DataRow("...+3,")]
    [DataRow("1...4,...+3")]
    [DataRow("...+0")]
    public void BindArray_OmittedBounds_RoundTripsExactly(string shape)
    {
        using var captured = BindingSnapshot.Capture(new Session().State.Context);
        var scope = new SnapshotBindingScope(captured);
        var expected = SymbolBinder.BindType(CilSyntaxParser.ParseType("int32[" + shape + "]"), scope).Type;
        Assert.IsGreaterThan(expected.LowerBounds.Count, expected.Sizes.Count);
        var spelling = new TypeSpeller(scope).Spell(expected);
        Assert.AreEqual("int32[" + shape + "]", spelling);
        Assert.AreEqual(expected, SymbolBinder.BindType(CilSyntaxParser.ParseType(spelling), scope).Type);
    }

    /// <summary>
    /// Bounds distinguish otherwise identical member signatures and entries keyed by their types.
    /// </summary>
    [TestMethod]
    public void ArrayIdentity_DistinguishesSizesBoundsAndTheirPresence()
    {
        var context = new Session().State.Context;
        using var captured = BindingSnapshot.Capture(context);
        var scope = new SnapshotBindingScope(captured);
        var spellings = new[] { "int32[...]", "int32[0...]", "int32[1...]", "int32[3]", "int32[4]", "int32[1...3]" };
        var symbols = spellings.Select(text => SymbolBinder.BindType(CilSyntaxParser.ParseType(text), scope).Type).ToArray();
        Assert.HasCount(spellings.Length, symbols.ToHashSet());
        for (var first = 0; first < symbols.Length; first++)
        {
            for (var second = 0; second < symbols.Length; second++)
            {
                Assert.AreEqual(first == second, SymbolIdentity.Equal(symbols[first], symbols[second]));
            }
        }

        Assert.IsTrue(SymbolRelations.IsAssignable(symbols[3], symbols[4], scope), "Runtime array values do not carry signature sizes.");
    }
}
