using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Redefining a type that other definitions depend on rebuilds them against the new type, as
/// new identities, and only when every one of them still compiles; otherwise nothing changes.
/// </summary>
[TestClass]
public sealed class TypeReplacementTests
{
    private static readonly string[] Point =
    [
        ".class public Point {",
        ".field public int32 X",
        ".method public instance void .ctor(int32 x) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg x; stfld int32 Point::X; ret }",
        ".method public instance int32 Get() { ldarg.0; ldfld int32 Point::X; ret }",
        "}",
    ];

    private static readonly string[] Dependents =
    [
        ".method int32 Make(int32 v) { ldarg v; newobj instance void Point::.ctor(int32); call instance int32 Point::Get(); ret }",
        ".class public Line {",
        ".field public class Point A",
        ".method public static int32 Use() { ldc.i4 5; call int32 Make(int32); ret }",
        "}",
        ".method int32 Proxy() { call int32 Line::Use(); ret }",
    ];

    private static Session Load(params string[] lines) => IlLines.Load(lines);

    private static object? Run(Session session, params string[] lines)
    {
        foreach (var line in IlLines.Expand(lines))
        {
            session.AddLine(line);
        }

        return session.Run().Value;
    }

    private static string Add(Session session, params string[] lines)
    {
        var last = "";
        foreach (var line in IlLines.Expand(lines))
        {
            last = session.AddLine(line).Message ?? last;
        }

        return last;
    }

    /// <summary>
    /// A compatible redefinition rebuilds every dependent, transitively, and names them.
    /// </summary>
    [TestMethod]
    public void Redefine_Compatible_RebuildsDependentsTransitively()
    {
        var session = Load([.. Point, .. Dependents]);
        Assert.AreEqual(5, Run(session, "call int32 Proxy()"));
        var oldMake = session.Methods.First(m => m.Signature.Name == "Make").Trampoline;
        var message = Add(session, ".class public Point {", ".field public int32 X",
            ".method public instance void .ctor(int32 x) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg x; ldc.i4 2; mul; stfld int32 Point::X; ret }",
            ".method public instance int32 Get() { ldarg.0; ldfld int32 Point::X; ret }", "}");
        Assert.AreEqual("replaced class Point; rebuilt method Make, class Line and method Proxy (existing instances and delegates keep the previous definitions)", message);
        Assert.AreEqual(10, Run(session, "call int32 Proxy()"));
        Assert.AreNotSame(oldMake, session.Methods.First(m => m.Signature.Name == "Make").Trampoline, "a rebuilt method is a new identity");
        Assert.AreEqual(2, session.TypeCount);
        Assert.HasCount(2, session.Methods);
    }

    /// <summary>
    /// A redefinition a dependent cannot use is refused, naming the dependent, and everything
    /// keeps running as before.
    /// </summary>
    [TestMethod]
    public void Redefine_Incompatible_IsRefusedAndNothingChanges()
    {
        var session = Load([.. Point, .. Dependents]);
        var before = session.Types.Select(t => t.RuntimeType).ToList();
        var message = Assert.ThrowsExactly<ReplException>(() => Add(session, ".class public Point {", ".field public int32 X", "}")).Message;
        Assert.StartsWith("cannot redefine class Point: method Make: ", message);
        Assert.Contains("declares no constructor", message);
        Assert.EndsWith("(redefine method Make first without it, or .reset)", message);
        Assert.IsNull(session.OpenType);
        Assert.AreSequenceEqual(before, session.Types.Select(t => t.RuntimeType).ToList());
        Assert.AreEqual(5, Run(session, "call int32 Proxy()"));
    }

    /// <summary>
    /// Making a field private breaks a dependent that reads it; the replacement is refused with
    /// the access rule in the message.
    /// </summary>
    [TestMethod]
    public void Redefine_NarrowedAccess_IsRefused()
    {
        var session = Load([.. Point, ".class public Reader {", ".method public static int32 Read(class Point p) { ldarg p; ldfld int32 Point::X; ret }", "}"]);
        var message = Assert.ThrowsExactly<ReplException>(() => Add(session, ".class public Point {", ".field private int32 X", "}")).Message;
        Assert.Contains("cannot redefine class Point: class Reader: int32 Point::X is private", message);
        Assert.AreEqual(2, session.TypeCount);
    }

    /// <summary>
    /// A cell that mentions the type is rebuilt with it, or the redefinition is refused when it
    /// no longer compiles.
    /// </summary>
    [TestMethod]
    public void Redefine_WithTheCellMentioningIt_RebuildsOrRefuses()
    {
        var session = Load(Point);
        session.AddLine("ldc.i4 3");
        session.AddLine("newobj instance void Point::.ctor(int32)");
        Add(session, ".class public Point {", ".field public int32 X",
            ".method public instance void .ctor(int32 x) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }", "}");
        Assert.AreEqual("[Point]", session.State.Stack.Render());
        var message = Assert.ThrowsExactly<ReplException>(() => Add(session, ".class public Point {", ".field public int32 X", "}")).Message;
        Assert.Contains("the cell body would no longer compile", message);
        Assert.Contains("(.clear the cell first)", message);
    }

    /// <summary>
    /// An instance made before the redefinition keeps the old type while new cells use the new one.
    /// </summary>
    [TestMethod]
    public void Redefine_RetainedInstance_KeepsTheOldType()
    {
        var session = Load(Point);
        var old = Run(session, "ldc.i4 1", "newobj instance void Point::.ctor(int32)")!;
        Add(session, ".class public Point {", ".field public int32 X", ".field public int32 Y",
            ".method public instance void .ctor(int32 x) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }", "}");
        var fresh = Run(session, "ldc.i4 1", "newobj instance void Point::.ctor(int32)")!;
        Assert.AreNotSame(old.GetType(), fresh.GetType());
        Assert.IsNull(old.GetType().GetField("Y"));
        Assert.IsNotNull(fresh.GetType().GetField("Y"));
    }

    /// <summary>
    /// Mutual references between top-level types: define A, define B using A, redefine A using B.
    /// </summary>
    [TestMethod]
    public void Redefine_MutualReferences_Work()
    {
        var session = Load(
            ".class public A {", ".method public static int32 Value() { ldc.i4 1; ret }", "}",
            ".class public B {", ".method public static int32 Twice() { call int32 A::Value(); ldc.i4 2; mul; ret }", "}");
        var message = Add(session, ".class public A {", ".method public static int32 Value() { ldc.i4 1; ret }", ".method public static int32 Four() { call int32 B::Twice(); ldc.i4 2; mul; ret }", "}");
        Assert.AreEqual("replaced class A; rebuilt class B (existing instances and delegates keep the previous definitions)", message);
        Assert.AreEqual(4, Run(session, "call int32 A::Four()"));
    }

    /// <summary>
    /// A redefinition that refers back to a dependent binds to that dependent's new identity,
    /// so no member of the group runs against a previous generation.
    /// </summary>
    [TestMethod]
    public void Redefine_CyclicReference_BindsToNewIdentities()
    {
        var session = Load(
            ".class public A {", ".method public static int32 Value() { ldc.i4 1; ret }", "}",
            ".class public B {", ".method public static int32 Twice() { call int32 A::Value(); ldc.i4 2; mul; ret }", "}");
        var message = Add(session, ".class public A {", ".method public static int32 Value() { ldc.i4 10; ret }", ".method public static int32 Four() { call int32 B::Twice(); ldc.i4 2; mul; ret }", "}");
        Assert.AreEqual("replaced class A; rebuilt class B (existing instances and delegates keep the previous definitions)", message);
        Assert.AreEqual(20, Run(session, "call int32 B::Twice()"));
        Assert.AreEqual(40, Run(session, "call int32 A::Four()"), "A.Four calls the rebuilt B, which calls the new A");
    }

    /// <summary>
    /// A same-signature method redefinition does not change the order in which a later
    /// replacement rebuilds callers, and every caller ends up on the new entry points.
    /// </summary>
    [TestMethod]
    public void Redefine_AfterMethodRedefinition_CallersFollowTheNewEntryPoints()
    {
        var session = Load(
            ".class public A {", ".method public static int32 Value() { ldc.i4 1; ret }", "}",
            ".method int32 F() { call int32 A::Value(); ret }",
            ".method int32 G() { call int32 F(); ret }",
            ".method int32 F() { call int32 A::Value(); ret }");
        var message = Add(session, ".class public A {", ".method public static int32 Value() { ldc.i4 10; ret }", "}");
        Assert.Contains("rebuilt method F and method G", message);
        Assert.AreEqual(10, Run(session, "call int32 F()"));
        Assert.AreEqual(10, Run(session, "call int32 G()"), "G calls the rebuilt F");
    }

    /// <summary>
    /// A pending cell that calls a session method whose signature names the type is rebuilt
    /// once the method has been, not against a mix of new types and old signatures.
    /// </summary>
    [TestMethod]
    public void Redefine_WithCellCallingAMethodOverTheType_RebuildsTheCellLast()
    {
        var session = Load(
            ".class public A {", ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }", "}",
            ".method class A Id(class A a) { ldarg a; ret }");
        session.AddLine("newobj instance void A::.ctor()");
        session.AddLine("call class A Id(class A)");
        var message = Add(session, ".class public A {", ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }", "}");
        Assert.Contains("rebuilt method Id", message);
        Assert.AreEqual("[A]", session.State.Stack.Render());
        Assert.IsNotNull(session.Run().Value);
    }

    /// <summary>
    /// A nested type the replacement drops stops resolving, so nothing can bind to it and a save
    /// has nothing stale to refuse.
    /// </summary>
    [TestMethod]
    public void Redefine_DroppingANestedType_RemovesItFromLookup()
    {
        var session = Load(".class public Outer {", ".class nested public Inner {", ".method public static int32 One() { ldc.i4 1; ret }", "}", "}");
        Assert.AreEqual(1, Run(session, "call int32 Outer/Inner::One()"));
        Add(session, ".class public Outer {", "}");
        Assert.Contains("not found", Assert.ThrowsExactly<ReplException>(() => session.AddLine("call int32 Outer/Inner::One()")).Message);
        Assert.IsFalse(session.TypeTable.TryResolve("Outer/Inner", false, false, out _));
        _ = AssemblyExporter.Write(session, "outer");
    }

    /// <summary>
    /// A type named in an attribute argument is a dependency: the annotated type is rebuilt
    /// against the new one, and the saved assembly names the new one.
    /// </summary>
    [TestMethod]
    public void Redefine_TypeNamedInAnAttributeArgument_RebuildsTheAnnotatedType()
    {
        var session = Load(
            ".class public Point { }",
            ".class public Line {",
            ".custom instance void [System.Runtime]System.Diagnostics.DebuggerTypeProxyAttribute::.ctor(class [System.Runtime]System.Type) = { type(Point) }",
            "}");
        var message = Add(session, ".class public Point {", ".field public int32 X", "}");
        Assert.Contains("rebuilt class Line", message);
        var line = session.Types[1].RuntimeType!;
        Assert.AreSame(session.Types[0].RuntimeType, line.GetCustomAttributesData()[0].ConstructorArguments[0].Value);
        _ = AssemblyExporter.Write(session, "annotated");
    }

    /// <summary>
    /// A family replaced twice maps every generation of its prototypes, so a dependent whose
    /// signature names it replays cleanly each time.
    /// </summary>
    [TestMethod]
    public void Redefine_Twice_WithATypedDependent()
    {
        var session = Load(".class public A { }", ".class public B {", ".method public static class A Id(class A a) { ldarg a; ret }", "}");
        Assert.Contains("rebuilt class B", Add(session, ".class public A {", ".field public int32 X", "}"));
        Assert.Contains("rebuilt class B", Add(session, ".class public A {", ".field public int32 Y", "}"));
        Assert.IsNotNull(session.Types[0].RuntimeType!.GetField("Y"));
        Assert.AreEqual(session.Types[0].RuntimeType, session.Types[1].RuntimeType!.GetMethod("Id")!.ReturnType);
    }

    /// <summary>
    /// A caller of a generic method keeps working through a replacement of the method's type.
    /// </summary>
    [TestMethod]
    public void Redefine_WithAGenericCaller()
    {
        var session = Load(
            ".class public A {", ".method public static !!0 Id<T>(!!0 v) { ldarg v; ret }", "}",
            ".class public B {", ".method public static int32 Use() { ldc.i4 7; call !!0 A::Id<int32>(!!0); ret }", "}");
        Assert.AreEqual(7, Run(session, "call int32 B::Use()"));
        Assert.Contains("rebuilt class B", Add(session, ".class public A {", ".method public static !!0 Id<T>(!!0 v) { ldarg v; ret }", "}"));
        Assert.AreEqual(7, Run(session, "call int32 B::Use()"));
        Assert.AreEqual(7, session.Types[1].RuntimeType!.GetMethod("Use")!.Invoke(null, null));
    }

    /// <summary>
    /// A derived type calling an inherited member keeps working through a replacement of the base.
    /// </summary>
    [TestMethod]
    public void Redefine_Base_WithADerivedCallerOfAnInheritedMember()
    {
        var session = Load(
            ".class public Base {", ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }", ".method public instance int32 F() { ldc.i4 1; ret }", "}",
            ".class public Derived extends Base {", ".method public instance void .ctor() { ldarg.0; call instance void Base::.ctor(); ret }", ".method public instance int32 G() { ldarg.0; call instance int32 Derived::F(); ret }", "}");
        Assert.Contains("rebuilt class Derived", Add(session, ".class public Base {", ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }", ".method public instance int32 F() { ldc.i4 2; ret }", "}"));
        Assert.AreEqual(2, Run(session, "newobj instance void Derived::.ctor()", "call instance int32 Derived::G()"));
    }

    /// <summary>
    /// A custom modifier naming a type is a dependency of the declaration that carries it.
    /// </summary>
    [TestMethod]
    public void Redefine_TypeNamedInAModifier_RebuildsTheDeclaration()
    {
        var session = Load(".class public A { }", ".class public B {", ".field public int32 modopt(A) V", "}");
        Assert.Contains("rebuilt class B", Add(session, ".class public A {", ".field public int32 X", "}"));
        Assert.AreEqual(session.Types[0].RuntimeType, session.Types[1].RuntimeType!.GetField("V")!.GetOptionalCustomModifiers()[0]);
        _ = AssemblyExporter.Write(session, "modified");
    }

    /// <summary>
    /// A type that appears only as a generic argument of a call is a dependency of the caller.
    /// </summary>
    [TestMethod]
    public void Redefine_TypeUsedOnlyAsAGenericArgument_RebuildsTheCaller()
    {
        var session = Load(".class public Point { }",
            ".class public Host {",
            ".method public static class [System.Runtime]System.Type TypeOf<T>() { ldtoken !!0; call class [System.Runtime]System.Type [System.Runtime]System.Type::GetTypeFromHandle(valuetype [System.Runtime]System.RuntimeTypeHandle); ret }",
            ".method public static class [System.Runtime]System.Type Get() { call class [System.Runtime]System.Type Host::TypeOf<class Point>(); ret }",
            "}");
        var message = Add(session, ".class public Point {", ".field public int32 X", "}");
        Assert.Contains("rebuilt class Host", message);
        Assert.AreSame(session.Types[0].RuntimeType, Run(session, "call class [System.Runtime]System.Type Host::Get()"));
        _ = AssemblyExporter.Write(session, "generic-argument");
    }
}
