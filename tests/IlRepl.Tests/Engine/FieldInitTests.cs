using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Field constants, literals, initonly stores, and type initializers behave as ILAsm
/// declares them: a constant is metadata, storage starts zero, and initonly holds.
/// </summary>
[TestClass]
public sealed class FieldInitTests
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
    /// A field initializer is a constant in metadata; the storage is still zero.
    /// </summary>
    [TestMethod]
    public void FieldConstant_IsMetadataNotStorage()
    {
        var session = Load(".class public Consts {", ".field public static int32 Answer = int32(42)", ".field public static literal string Name = \"consts\"", "}");
        var type = session.Types[0].RuntimeType!;
        var answer = type.GetField("Answer")!;
        Assert.IsTrue(answer.Attributes.HasFlag(FieldAttributes.HasDefault));
        Assert.IsFalse(answer.IsLiteral);
        Assert.AreEqual(42, session.Types[0].Declaration.Fields[0].DefaultValue);
        Assert.AreEqual(0, Run(session, "ldsfld int32 Consts::Answer"), "the runtime does not initialize storage from the constant");
        var name = type.GetField("Name")!;
        Assert.IsTrue(name.IsLiteral);
        Assert.AreEqual("consts", name.GetRawConstantValue());
    }

    /// <summary>
    /// A static initonly field is set by the type initializer and an instance one by the constructor.
    /// </summary>
    [TestMethod]
    public void InitOnly_SetByInitializers()
    {
        var session = Load(
            ".class public Fixed {",
            ".field public static initonly int32 S",
            ".field public initonly int32 V",
            ".method static void .cctor() { ldc.i4 7; stsfld int32 Fixed::S; ret }",
            ".method public instance void .ctor(int32 v) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg v; stfld int32 Fixed::V; ret }",
            "}");
        Assert.AreEqual(7, Run(session, "ldsfld int32 Fixed::S"));
        Assert.AreEqual(3, Run(session, "ldc.i4 3", "newobj instance void Fixed::.ctor(int32)", "ldfld int32 Fixed::V"));
        Assert.IsTrue(session.Types[0].RuntimeType!.GetField("S")!.IsInitOnly);
        Assert.Contains("static initonly", Assert.ThrowsExactly<ReplException>(() => Run(session, "ldc.i4 1", "stsfld int32 Fixed::S")).Message);
    }

    /// <summary>
    /// beforefieldinit lets the runtime run the initializer early; without it, the first access does.
    /// </summary>
    [TestMethod]
    public void BeforeFieldInit_IsCarried()
    {
        var session = Load(
            ".class public beforefieldinit Eager {", ".field public static int32 X", "}",
            ".class public Precise {", ".field public static int32 X", "}");
        Assert.IsTrue(session.Types[0].RuntimeType!.Attributes.HasFlag(TypeAttributes.BeforeFieldInit));
        Assert.IsFalse(session.Types[1].RuntimeType!.Attributes.HasFlag(TypeAttributes.BeforeFieldInit));
    }

    /// <summary>
    /// An instance field of a struct type starts zeroed and is read through its address.
    /// </summary>
    [TestMethod]
    public void StructField_StartsZeroed()
    {
        var session = Load(
            ".class public sequential sealed Inner extends [System.Runtime]System.ValueType {", ".field public int32 V", "}",
            ".class public Box {", ".field public valuetype Inner In", ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }", "}");
        Assert.AreEqual(0, Run(session, "newobj instance void Box::.ctor()", "ldflda valuetype Inner Box::In", "ldfld int32 Inner::V"));
        Assert.AreEqual(9, Run(session, ".locals init (class Box b)", "newobj instance void Box::.ctor()", "stloc b", "ldloc b", "ldflda valuetype Inner Box::In", "ldc.i4 9", "stfld int32 Inner::V", "ldloc b", "ldflda valuetype Inner Box::In", "ldfld int32 Inner::V"));
    }
}
