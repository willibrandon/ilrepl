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
}
