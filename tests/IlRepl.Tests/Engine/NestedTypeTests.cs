using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Nested types: their runtime names, their visibility, and their use from the enclosing type
/// and from cells.
/// </summary>
[TestClass]
public sealed class NestedTypeTests
{
    private static Session Load(params string[] lines) => IlLines.Load(lines);

    private static object? Run(Session session, params string[] lines)
    {
        foreach (var line in IlLines.Expand(lines))
        {
            session.AddLine(line);
        }

        return session.Run().Value;
    }

    /// <summary>
    /// A nested struct is named Outer/Inner in IL and Outer+Inner at run time, and is one family.
    /// </summary>
    [TestMethod]
    public void Nested_NamesAndFamily()
    {
        var session = Load(
            ".class public Outer {",
            ".class nested public sequential sealed Inner extends [System.Runtime]System.ValueType {",
            ".field public int32 V",
            "}",
            ".field public static valuetype Outer/Inner Last",
            ".method public static int32 Set(int32 v) { .locals init (valuetype Outer/Inner i); ldloca i; ldarg v; stfld int32 Outer/Inner::V; ldloc i; stsfld valuetype Outer/Inner Outer::Last; ldarg v; ret }",
            "}");
        Assert.HasCount(1, session.Types);
        Assert.AreEqual(2, session.TypeCount);
        var outer = session.Types[0].RuntimeType!;
        var inner = session.Types[0].Types["Outer/Inner"];
        Assert.AreEqual("Outer+Inner", inner.FullName);
        Assert.AreSame(outer, inner.DeclaringType);
        Assert.AreSame(outer.Assembly, inner.Assembly);
        Assert.AreEqual(5, Run(session, "ldc.i4 5", "call int32 Outer::Set(int32)"));
        Assert.AreEqual(5, Run(session, "ldsflda valuetype Outer/Inner Outer::Last", "ldfld int32 Outer/Inner::V"));
    }

    /// <summary>
    /// A nested private type is usable by its enclosing type and refused to a cell.
    /// </summary>
    [TestMethod]
    public void NestedPrivate_VisibleInsideOnly()
    {
        var session = Load(
            ".class public Outer {",
            ".class nested private Secret {",
            ".method public static int32 Value() { ldc.i4 41; ret }",
            "}",
            ".method public static int32 Reveal() { call int32 Outer/Secret::Value(); ldc.i4 1; add; ret }",
            "}");
        Assert.AreEqual(42, Run(session, "call int32 Outer::Reveal()"));
        Assert.Contains("is nested private", Assert.ThrowsExactly<ReplException>(() => session.AddLine("call int32 Outer/Secret::Value()")).Message);
        Assert.IsTrue(session.Types[0].Types["Outer/Secret"].IsNestedPrivate);
    }

    /// <summary>
    /// A nested type is referenced before its declaration inside the family, and the
    /// placeholder becomes the real type.
    /// </summary>
    [TestMethod]
    public void Nested_ForwardReference_Resolves()
    {
        var session = Load(
            ".class public Tree {",
            ".field public class Tree/Node Root",
            ".class nested public Node {",
            ".field public int32 Value",
            ".method public instance void .ctor(int32 v) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg v; stfld int32 Tree/Node::Value; ret }",
            "}",
            ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldc.i4 8; newobj instance void Tree/Node::.ctor(int32); stfld class Tree/Node Tree::Root; ret }",
            "}");
        Assert.AreEqual(8, Run(session, "newobj instance void Tree::.ctor()", "ldfld class Tree/Node Tree::Root", "ldfld int32 Tree/Node::Value"));
        Assert.AreSame(session.Types[0].Types["Tree/Node"], session.Types[0].RuntimeType!.GetField("Root")!.FieldType);
    }

    /// <summary>
    /// Three levels of nesting keep their paths.
    /// </summary>
    [TestMethod]
    public void Nested_ThreeLevels()
    {
        var session = Load(
            ".class public A {",
            ".class nested public B {",
            ".class nested public C {",
            ".method public static int32 Deep() { ldc.i4 3; ret }",
            "}",
            "}",
            "}");
        Assert.AreEqual(3, session.TypeCount);
        Assert.AreEqual("A+B+C", session.Types[0].Types["A/B/C"].FullName);
        Assert.AreEqual(3, Run(session, "call int32 A/B/C::Deep()"));
        Assert.AreEqual(BindingFlags.Public, session.Types[0].Types["A/B/C"].IsNestedPublic ? BindingFlags.Public : BindingFlags.NonPublic);
    }

    /// <summary>
    /// A nested generic type redeclares the enclosing parameters first and its arity suffix
    /// counts only the ones it introduces (ECMA I.10.7.1); references carry the total list.
    /// </summary>
    [TestMethod]
    public void NestedGeneric_ArityAndReferences()
    {
        var session = Load(
            ".class public Outer`1<T> {",
            ".class nested public Inner`1<T, U> {",
            ".field public !0 A",
            ".field public !1 B",
            ".method public instance void .ctor(!0 a, !1 b) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg a; stfld !0 class Outer`1/Inner`1<!0, !1>::A; ldarg.0; ldarg b; stfld !1 class Outer`1/Inner`1<!0, !1>::B; ret }",
            "}",
            ".class nested public Same<T> { }",
            "}");
        var outer = session.Types[0].RuntimeType!;
        var inner = session.Types[0].Types["Outer`1/Inner`1"];
        Assert.AreEqual("Inner`1", inner.Name);
        Assert.HasCount(2, inner.GetGenericArguments(), "the nested type carries both parameters");
        Assert.AreEqual("Same", session.Types[0].Types["Outer`1/Same"].Name, "no arity suffix when nothing is introduced");
        Assert.AreEqual("x", Run(session, "ldc.i4 1", "ldstr \"x\"", "newobj instance void class Outer`1/Inner`1<int32, string>::.ctor(!0, !1)", "ldfld !1 class Outer`1/Inner`1<int32, string>::B"));
        Assert.AreEqual(1, Run(session, "ldc.i4 1", "ldstr \"x\"", "newobj instance void class Outer`1/Inner`1<int32, string>::.ctor(!0, !1)", "ldfld !0 class Outer`1/Inner`1<int32, string>::A"));
        Assert.AreSame(outer, inner.DeclaringType);
        var wrong = Load(".class public Outer`1<T> {");
        Assert.Contains("Inner`1 needs 2 parameters, or write Inner for the 0 it introduces", Assert.ThrowsExactly<ReplException>(() => wrong.AddLine(".class nested public Inner`1<U> {")).Message);
    }
}
