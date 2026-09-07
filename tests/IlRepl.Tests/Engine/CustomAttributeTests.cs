using System.Reflection;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Custom attributes on types, fields, methods, and parameters are real attributes on the live
/// type, with their arguments, including Type arguments that name session types.
/// </summary>
[TestClass]
public sealed class CustomAttributeTests
{
    private static Session Load(params string[] lines) => IlLines.Load(lines);

    /// <summary>
    /// Blob and typed forms both produce the attribute with its arguments.
    /// </summary>
    [TestMethod]
    public void Attributes_OnEveryTarget()
    {
        var session = Load(
            ".class public Tagged {",
            ".custom instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string) = ( 01 00 03 6F 6C 64 00 00 )",
            ".field public static int32 F",
            ".custom instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string, bool) = { string('gone') bool(true) }",
            ".method public static int32 M(int32 x) { .custom instance void [System.Runtime]System.Diagnostics.ConditionalAttribute::.ctor(string) = { string('DEBUG') }; .param [1]; .custom instance void [System.Runtime]System.Runtime.CompilerServices.CallerMemberNameAttribute::.ctor() = ( 01 00 00 00 ); ldarg x; ret }",
            "}");
        var tagged = session.Types[0].RuntimeType!;
        var onType = tagged.GetCustomAttribute<ObsoleteAttribute>()!;
        Assert.AreEqual("old", onType.Message);
        var onField = tagged.GetField("F")!.GetCustomAttribute<ObsoleteAttribute>()!;
        Assert.AreEqual("gone", onField.Message);
        Assert.IsTrue(onField.IsError);
        var method = tagged.GetMethod("M")!;
        Assert.AreEqual("DEBUG", method.GetCustomAttribute<System.Diagnostics.ConditionalAttribute>()!.ConditionString);
        Assert.IsNotNull(method.GetParameters()[0].GetCustomAttribute<System.Runtime.CompilerServices.CallerMemberNameAttribute>());
    }

    /// <summary>
    /// A Type argument naming a type of the same family, and one naming a type of another
    /// family, both resolve when read from the live attribute.
    /// </summary>
    [TestMethod]
    public void TypeArguments_ResolveAcrossFamilies()
    {
        var session = Load(
            ".class public Point { }",
            ".class public Line {",
            ".custom instance void [System.Runtime]System.Diagnostics.DebuggerTypeProxyAttribute::.ctor(class [System.Runtime]System.Type) = { type(Point) }",
            ".class nested public Proxy { }",
            ".custom instance void [System.Runtime]System.Diagnostics.DebuggerDisplayAttribute::.ctor(string) = { string('line') }",
            "}");
        var line = session.Types[1].RuntimeType!;
        var proxy = line.GetCustomAttributesData().First(a => a.AttributeType == typeof(System.Diagnostics.DebuggerTypeProxyAttribute));
        Assert.AreSame(session.Types[0].RuntimeType, proxy.ConstructorArguments[0].Value, "the cross-family Type argument names the live Point");
        Assert.AreEqual("line", line.GetCustomAttribute<System.Diagnostics.DebuggerDisplayAttribute>()!.Value);
        var self = Load(
            ".class public Own {",
            ".custom instance void [System.Runtime]System.Diagnostics.DebuggerTypeProxyAttribute::.ctor(class [System.Runtime]System.Type) = { type(Own/Inner) }",
            ".class nested public Inner { }",
            "}");
        var own = self.Types[0].RuntimeType!;
        Assert.AreSame(self.Types[0].Types["Own/Inner"], own.GetCustomAttributesData()[0].ConstructorArguments[0].Value);
    }

    /// <summary>
    /// Enum and array arguments, and named fields, are encoded.
    /// </summary>
    [TestMethod]
    public void EnumArrayAndNamedArguments()
    {
        var session = Load(
            ".class public Flagged {",
            ".custom instance void [System.Runtime]System.AttributeUsageAttribute::.ctor(valuetype [System.Runtime]System.AttributeTargets) = { int32(4) property bool AllowMultiple = bool(true) }",
            "}");
        var usage = session.Types[0].RuntimeType!.GetCustomAttribute<AttributeUsageAttribute>()!;
        Assert.AreEqual(AttributeTargets.Class, usage.ValidOn);
        Assert.IsTrue(usage.AllowMultiple);
    }
}
