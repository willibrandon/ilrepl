using IlRepl.Engine;
using IlRepl.Engine.Binding;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Tests for <see cref="SymbolRelations"/> and <see cref="GenericConstraints"/>: relations over
/// symbols agree with the runtime's answers for loaded and session types, and constraint
/// verdicts agree with the runtime's construction.
/// </summary>
[TestClass]
public sealed class SymbolRelationsTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

    /// <summary>
    /// Assignability over framework types agrees with the runtime for a matrix of pairs.
    /// </summary>
    [TestMethod]
    public void IsAssignable_FrameworkPairs_AgreeWithTheRuntime()
    {
        using var snapshot = BindingSnapshot.Capture(Context);
        var scope = new SnapshotBindingScope(snapshot);
        var types = new[]
        {
            typeof(object), typeof(string), typeof(int), typeof(long), typeof(int?), typeof(ValueType), typeof(Enum), typeof(Environment.SpecialFolder),
            typeof(Exception), typeof(ArgumentException), typeof(IDisposable), typeof(System.IO.Stream), typeof(System.IO.MemoryStream),
            typeof(IEnumerable<string>), typeof(IEnumerable<object>), typeof(List<string>), typeof(IList<string>), typeof(IReadOnlyList<string>),
            typeof(string[]), typeof(object[]), typeof(int[]), typeof(int[,]), typeof(Array), typeof(System.Collections.IList), typeof(System.Collections.IEnumerable),
            typeof(Action<string>), typeof(Action<object>), typeof(Delegate), typeof(Func<object>), typeof(Func<string>), typeof(IComparable<int>),
            typeof(KeyValuePair<string, int>), typeof(Dictionary<string, int>), typeof(IDictionary<string, int>), typeof(IReadOnlyCollection<string>),
        };
        var disagreements = new List<string>();
        foreach (var from in types)
        {
            foreach (var to in types)
            {
                var expected = TypeRelations.IsAssignable(from, to, TypeTable.Empty);
                var actual = SymbolRelations.IsAssignable(RuntimeSymbolImporter.Import(from), RuntimeSymbolImporter.Import(to), scope);
                if (expected != actual)
                {
                    disagreements.Add($"{TypeNameFormatter.Pretty(from)} -> {TypeNameFormatter.Pretty(to)}: runtime {expected}, symbols {actual}");
                }
            }
        }

        Assert.IsEmpty(disagreements, string.Join("\n", disagreements));
    }

    /// <summary>
    /// The base chain and the interfaces of a session type come from its declaration on both sides.
    /// </summary>
    [TestMethod]
    public void BaseAndInterfaces_SessionTypes_AgreeWithTheRuntime()
    {
        var session = IlLines.Load(
            ".class interface public abstract IShape { }",
            ".class public abstract Shape implements IShape { }",
            ".class public Circle extends Shape implements [System.Runtime]System.IDisposable {",
            ".method public virtual instance void Dispose() { ret }",
            "}",
            ".class interface public abstract IOut`1<+T> { }",
            ".class public Box`1<T> implements class IOut`1<!0> { }");
        var context = session.State.Context;
        using var snapshot = BindingSnapshot.Capture(context);
        var scope = new SnapshotBindingScope(snapshot);
        var table = session.TypeTable;
        Type Find(string name) => table.TryResolve(name, false, false, out var t) ? t : throw new AssertFailedException(name);
        var circle = Find("Circle");
        var shape = Find("Shape");
        var ishape = Find("IShape");
        var box = Find("Box`1");
        var iout = Find("IOut`1");

        Assert.AreEqual(RuntimeSymbolImporter.Import(shape), scope.BaseOf(RuntimeSymbolImporter.Import(circle)));
        Assert.AreEqual(TypeSymbol.Object, scope.BaseOf(RuntimeSymbolImporter.Import(shape)));
        Assert.IsNull(scope.BaseOf(RuntimeSymbolImporter.Import(ishape)));
        var all = SymbolRelations.AllInterfacesOf(RuntimeSymbolImporter.Import(circle), scope);
        Assert.HasCount(2, all);
        Assert.Contains(RuntimeSymbolImporter.Import(typeof(IDisposable)), all);
        Assert.Contains(RuntimeSymbolImporter.Import(ishape), all);
        Assert.IsTrue(SymbolRelations.IsSubclassOf(RuntimeSymbolImporter.Import(circle), RuntimeSymbolImporter.Import(shape), scope));
        Assert.IsFalse(SymbolRelations.IsSubclassOf(RuntimeSymbolImporter.Import(shape), RuntimeSymbolImporter.Import(circle), scope));

        var boxOfString = RuntimeSymbolImporter.Import(box.MakeGenericType(typeof(string)));
        var ioutOfObject = RuntimeSymbolImporter.Import(iout.MakeGenericType(typeof(object)));
        Assert.IsTrue(SymbolRelations.IsAssignable(boxOfString, ioutOfObject, scope), "IOut is covariant");
        Assert.IsFalse(SymbolRelations.IsAssignable(RuntimeSymbolImporter.Import(box.MakeGenericType(typeof(int))), ioutOfObject, scope), "variance never applies to value types");
        Assert.IsTrue(SymbolRelations.IsAssignable(RuntimeSymbolImporter.Import(circle.MakeArrayType()), RuntimeSymbolImporter.Import(shape.MakeArrayType()), scope));
        Assert.IsTrue(SymbolRelations.IsAssignable(RuntimeSymbolImporter.Import(circle), TypeSymbol.Object, scope));
        Assert.IsFalse(SymbolRelations.IsAssignable(RuntimeSymbolImporter.Import(circle), RuntimeSymbolImporter.Import(typeof(IComparable)), scope));
    }

    /// <summary>
    /// Substitution rewrites a definition's parameters inside every constructed form.
    /// </summary>
    [TestMethod]
    public void Substitute_RewritesEveryForm()
    {
        var list = RuntimeSymbolImporter.Import(typeof(List<>));
        var t = RuntimeSymbolImporter.Import(typeof(List<>).GetGenericArguments()[0]);
        var int32 = TypeSymbol.Primitive("int32");
        TypeSymbol Sub(TypeSymbol type) => SymbolRelations.SubstituteTypeParameters(type, list.Definition, [int32]);
        Assert.AreEqual(int32, Sub(t));
        Assert.AreEqual(TypeSymbol.SzArray(int32), Sub(TypeSymbol.SzArray(t)));
        Assert.AreEqual(TypeSymbol.ByRef(int32), Sub(TypeSymbol.ByRef(t)));
        Assert.AreEqual(TypeSymbol.Construct(list, [int32]), Sub(TypeSymbol.Construct(list, [t])));
        Assert.AreEqual(RuntimeSymbolImporter.Import(typeof(List<int>)), Sub(TypeSymbol.Construct(list, [t])));
        var other = RuntimeSymbolImporter.Import(typeof(Dictionary<,>).GetGenericArguments()[0]);
        Assert.AreEqual(other, Sub(other), "another owner's parameter is untouched");

        var add = RuntimeSymbolImporter.Import(typeof(List<>).GetMethod("Add")!);
        var addOfInt = SymbolRelations.Instantiate(add, RuntimeSymbolImporter.Import(typeof(List<int>)), []);
        Assert.AreEqual(RuntimeSymbolImporter.Import(typeof(List<int>).GetMethod("Add")!), addOfInt);
        Assert.AreEqual(int32, addOfInt.Parameters[0].Type);
    }

    /// <summary>
    /// Constraint verdicts agree with the runtime's construction over a positive and negative matrix.
    /// </summary>
    [TestMethod]
    public void GenericConstraints_AgreeWithRuntimeConstruction()
    {
        using var snapshot = BindingSnapshot.Capture(Context);
        var scope = new SnapshotBindingScope(snapshot);
        var owners = new[] { typeof(Nullable<>), typeof(NeedsClass<>), typeof(NeedsStruct<>), typeof(NeedsNew<>), typeof(NeedsComparable<>), typeof(NeedsStream<>), typeof(List<>) };
        var arguments = new[] { typeof(int), typeof(string), typeof(object), typeof(int?), typeof(System.IO.MemoryStream), typeof(System.IO.Stream), typeof(Exception), typeof(int[]), typeof(IDisposable), typeof(Environment.SpecialFolder), typeof(KeyValuePair<int, int>) };
        var disagreements = new List<string>();
        foreach (var owner in owners)
        {
            var parameter = scope.GenericParameterDeclarations(RuntimeSymbolImporter.Import(owner))[0];
            foreach (var argument in arguments)
            {
                bool expected;
                try
                {
                    owner.MakeGenericType(argument);
                    expected = true;
                }
                catch (ArgumentException)
                {
                    expected = false;
                }

                var symbol = RuntimeSymbolImporter.Import(argument);
                var actual = GenericConstraints.Satisfies(parameter, symbol, c => SymbolRelations.SubstituteTypeParameters(c, parameter.Owner, [symbol]), scope);
                if (expected != actual)
                {
                    disagreements.Add($"{TypeNameFormatter.Pretty(owner)}<{TypeNameFormatter.Pretty(argument)}>: runtime {expected}, symbols {actual}");
                }
            }
        }

        Assert.IsEmpty(disagreements, string.Join("\n", disagreements));
    }

    /// <summary>
    /// A generic parameter argument is judged by what its own constraints promise, through a chain.
    /// </summary>
    [TestMethod]
    public void GenericConstraints_ParameterArguments_FollowTheirConstraints()
    {
        using var snapshot = BindingSnapshot.Capture(Context);
        var scope = new SnapshotBindingScope(snapshot);
        var needsClass = scope.GenericParameterDeclarations(RuntimeSymbolImporter.Import(typeof(NeedsClass<>)))[0];
        var needsStream = scope.GenericParameterDeclarations(RuntimeSymbolImporter.Import(typeof(NeedsStream<>)))[0];
        var chained = RuntimeSymbolImporter.Import(typeof(Chain<,>).GetGenericArguments()[1]);
        var streamOnly = RuntimeSymbolImporter.Import(typeof(StreamOnly<>).GetGenericArguments()[0]);
        var unconstrained = RuntimeSymbolImporter.Import(typeof(List<>).GetGenericArguments()[0]);
        Assert.IsTrue(GenericConstraints.Satisfies(needsClass, streamOnly, c => c, scope), "T : Stream proves a reference type without the class flag");
        Assert.IsTrue(GenericConstraints.Satisfies(needsClass, chained, c => c, scope), "S : T, T : Stream proves it through the chain");
        Assert.IsTrue(GenericConstraints.Satisfies(needsStream, chained, c => c, scope), "S : T, T : Stream satisfies a Stream constraint");
        Assert.IsFalse(GenericConstraints.Satisfies(needsClass, unconstrained, c => c, scope), "an unconstrained parameter proves nothing");
        Assert.IsFalse(GenericConstraints.Satisfies(needsStream, unconstrained, c => c, scope));
    }

    private sealed class NeedsClass<T> where T : class
    {
    }

    private sealed class NeedsStruct<T> where T : struct
    {
    }

    private sealed class NeedsNew<T> where T : new()
    {
    }

    private sealed class NeedsComparable<T> where T : IComparable<T>
    {
    }

    private sealed class NeedsStream<T> where T : System.IO.Stream
    {
    }

    private sealed class StreamOnly<T> where T : System.IO.Stream
    {
    }

    private sealed class Chain<T, S> where T : System.IO.Stream where S : T
    {
    }
}
