using System.Reflection;
using System.Runtime.CompilerServices;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Signature details survive into the live type: non-vector arrays, custom modifiers,
/// parameter attributes, and defaults set by .param.
/// </summary>
[TestClass]
public sealed class SignatureMetadataTests
{
    private static Session Load(params string[] lines) => IlLines.Load(lines);

    /// <summary>
    /// int32[0...] is a rank-one array, not a vector, on a field, a parameter, a return, and
    /// both overloads.
    /// </summary>
    [TestMethod]
    public void NonVectorArrays_AreKept()
    {
        var session = Load(
            ".class public Arrays {",
            ".field public static int32[0...] NonVector",
            ".method public static int32[0...] Make() { ldnull; ret }",
            ".method public static int32 Over(int32[] a) { ldc.i4 1; ret }",
            ".method public static int32 Over(int32[0...] a) { ldc.i4 2; ret }",
            "}");
        var arrays = session.Types[0].RuntimeType!;
        Assert.IsFalse(arrays.GetField("NonVector")!.FieldType.IsSZArray);
        Assert.AreEqual(1, arrays.GetField("NonVector")!.FieldType.GetArrayRank());
        Assert.IsFalse(arrays.GetMethod("Make")!.ReturnType.IsSZArray);
        Assert.AreEqual(2, arrays.GetMethods().Count(m => m.Name == "Over"));
        Assert.AreEqual(2, arrays.GetMethod("Over", [typeof(int).MakeArrayType(1)])!.Invoke(null, [null]));
        Assert.AreEqual(1, arrays.GetMethod("Over", [typeof(int[])])!.Invoke(null, [null]));
    }

    /// <summary>
    /// modreq and modopt are kept on fields, parameters, and returns.
    /// </summary>
    [TestMethod]
    public void Modifiers_AreKept()
    {
        var session = Load(
            ".class public Mods {",
            ".field public static int32 modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile) V",
            ".method public static string modreq([System.Runtime]System.Runtime.CompilerServices.IsVolatile) M(int32 modopt([System.Runtime]System.Runtime.CompilerServices.IsVolatile) x) { ldstr \"m\"; ret }",
            "}");
        var mods = session.Types[0].RuntimeType!;
        Assert.AreEqual(typeof(IsVolatile), mods.GetField("V")!.GetRequiredCustomModifiers()[0]);
        var m = mods.GetMethod("M")!;
        Assert.AreEqual(typeof(IsVolatile), m.ReturnParameter.GetRequiredCustomModifiers()[0]);
        Assert.AreEqual(typeof(IsVolatile), m.GetParameters()[0].GetOptionalCustomModifiers()[0]);
    }

    /// <summary>
    /// .param sets a default without making the parameter optional, and [in]/[out] are kept.
    /// </summary>
    [TestMethod]
    public void ParamDefaultsAndAttributes_AreKept()
    {
        var session = Load(
            ".class public Params {",
            ".method public static int32 M([in] int32 a, [out] int32& b, int32 c) { .param [3] = int32(7); ldarg b; ldc.i4 0; stind.i4; ldarg a; ret }",
            "}");
        var parameters = session.Types[0].RuntimeType!.GetMethod("M")!.GetParameters();
        Assert.IsTrue(parameters[0].IsIn);
        Assert.IsTrue(parameters[1].IsOut);
        Assert.AreEqual(7, parameters[2].DefaultValue);
        Assert.IsTrue(parameters[2].HasDefaultValue);
        Assert.IsFalse(parameters[2].IsOptional, "a default does not make a parameter optional");
        Assert.AreEqual("a", parameters[0].Name);
    }

    /// <summary>
    /// A vararg member is accepted where the runtime supports it and refused with the reason elsewhere.
    /// </summary>
    [TestMethod]
    public void VarArgMember_FollowsTheRuntime()
    {
        var lines = IlLines.Expand(".class public Va {", ".method public static vararg int32 Count(int32 first) { ldarg first; ret }").ToList();
        if (OperatingSystem.IsWindows())
        {
            var session = new Session();
            foreach (var line in lines)
            {
                session.AddLine(line);
            }

            Assert.AreEqual("end of class Va", session.AddLine("}").Message);
            Assert.IsTrue(session.Types[0].RuntimeType!.GetMethod("Count")!.CallingConvention.HasFlag(CallingConventions.VarArgs));
        }
        else
        {
            var session = new Session();
            foreach (var line in lines)
            {
                session.AddLine(line);
            }

            Assert.Contains("only supports the vararg calling convention on Windows", Assert.ThrowsExactly<ReplException>(() => session.AddLine("}")).Message);
        }
    }
}
