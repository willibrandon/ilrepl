using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Class members and session methods call each other through an explicit binding, and a
/// compatible session-method redefinition reaches the class members already calling it.
/// </summary>
[TestClass]
public sealed class InteropTests
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
    /// A class method calls a session method by name, and a session method uses a class.
    /// </summary>
    [TestMethod]
    public void ClassAndSessionMethods_CallEachOther()
    {
        var session = Load(
            ".method int32 Twice(int32 n) { ldarg n; ldc.i4 2; mul; ret }",
            ".class public Calc {",
            ".field public int32 Seed",
            ".method public instance void .ctor(int32 seed) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg seed; stfld int32 Calc::Seed; ret }",
            ".method public instance int32 Doubled() { ldarg.0; ldfld int32 Calc::Seed; call int32 Twice(int32); ret }",
            "}",
            ".method int32 UseCalc(int32 seed) { ldarg seed; newobj instance void Calc::.ctor(int32); call instance int32 Calc::Doubled(); ret }");
        Assert.AreEqual(14, Run(session, "ldc.i4 7", "call int32 UseCalc(int32)"));
    }

    /// <summary>
    /// Redefining a session method with the same signature reaches a class member already bound to it.
    /// </summary>
    [TestMethod]
    public void RedefineSessionMethod_ReachesClassCallers()
    {
        var session = Load(
            ".method int32 Twice(int32 n) { ldarg n; ldc.i4 2; mul; ret }",
            ".class public Calc {",
            ".method public static int32 Six() { ldc.i4 3; call int32 Twice(int32); ret }",
            "}");
        Assert.AreEqual(6, Run(session, "call int32 Calc::Six()"));
        foreach (var line in IlLines.Expand(".method int32 Twice(int32 n) { ldarg n; ldc.i4 3; mul; ret }"))
        {
            session.AddLine(line);
        }

        Assert.AreEqual(9, Run(session, "call int32 Calc::Six()"), "the class member calls through the trampoline");
    }

    /// <summary>
    /// A session method that mentions a type is rebuilt when the type is redefined, and a
    /// signature change to a session method a class calls is refused as before.
    /// </summary>
    [TestMethod]
    public void RedefineSessionMethodSignature_ClassCallerIsChecked()
    {
        var session = Load(
            ".method int32 Twice(int32 n) { ldarg n; ldc.i4 2; mul; ret }",
            ".class public Calc {",
            ".method public static int32 Six() { ldc.i4 3; call int32 Twice(int32); ret }",
            "}");
        var message = Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method int64 Twice(int64 n) {")).Message;
        Assert.Contains("cannot redefine Twice", message);
        Assert.Contains("class Calc", message);
    }

    /// <summary>
    /// A session method takes and returns a session type.
    /// </summary>
    [TestMethod]
    public void SessionMethod_UsesSessionTypesInItsSignature()
    {
        var session = Load(
            ".class public sequential sealed Pair extends [System.Runtime]System.ValueType {",
            ".field public int32 A",
            ".field public int32 B",
            "}",
            ".method valuetype Pair Make(int32 a, int32 b) { .locals init (valuetype Pair p); ldloca p; ldarg a; stfld int32 Pair::A; ldloca p; ldarg b; stfld int32 Pair::B; ldloc p; ret }",
            ".method int32 Sum(valuetype Pair p) { ldarg p; ldfld int32 Pair::A; ldarg p; ldfld int32 Pair::B; add; ret }");
        Assert.AreEqual(7, Run(session, "ldc.i4 3", "ldc.i4 4", "call valuetype Pair Make(int32, int32)", "call int32 Sum(valuetype Pair)"));
    }
}
