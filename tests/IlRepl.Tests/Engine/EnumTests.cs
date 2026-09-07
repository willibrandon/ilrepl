using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Enums declared with .class: the value__ field, literal members, the underlying type, and
/// the runtime's view of them.
/// </summary>
[TestClass]
public sealed class EnumTests
{
    private static readonly string[] Color =
    [
        ".class public enum Color {",
        ".field public specialname rtspecialname int32 value__",
        ".field public static literal valuetype Color Red = int32(0)",
        ".field public static literal valuetype Color Green = int32(1)",
        ".field public static literal valuetype Color Blue = int32(2)",
        "}",
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

    /// <summary>
    /// The runtime sees an enum with its names and values.
    /// </summary>
    [TestMethod]
    public void Enum_IsAnEnumAtRuntime()
    {
        var session = Load(Color);
        var color = session.Types[0].RuntimeType!;
        Assert.IsTrue(color.IsEnum);
        Assert.AreEqual(typeof(int), Enum.GetUnderlyingType(color));
        Assert.AreSequenceEqual(["Red", "Green", "Blue"], Enum.GetNames(color));
        Assert.AreEqual(1, (int)color.GetField("Green")!.GetRawConstantValue()!);
        Assert.AreEqual("Green", Run(session, "ldc.i4 1", "box Color")!.ToString());
        Assert.AreEqual("Blue", Run(session, "ldc.i4 2", "box Color")!.ToString());
        Assert.Contains("Blue is a literal; it has no storage", Assert.ThrowsExactly<ReplException>(() => session.AddLine("ldsfld valuetype Color Color::Blue")).Message);
        Assert.Contains(": ldc.i4 2", Assert.ThrowsExactly<ReplException>(() => session.AddLine("ldsfld valuetype Color Color::Blue")).Message);
    }

    /// <summary>
    /// A wider underlying type and the flags attribute are carried.
    /// </summary>
    [TestMethod]
    public void Enum_UnderlyingTypeAndFlags()
    {
        var session = Load(
            ".class public enum Big {",
            ".custom instance void [System.Runtime]System.FlagsAttribute::.ctor() = ( 01 00 00 00 )",
            ".field public specialname rtspecialname int64 value__",
            ".field public static literal valuetype Big A = int64(1)",
            ".field public static literal valuetype Big B = int64(2)",
            "}");
        var big = session.Types[0].RuntimeType!;
        Assert.AreEqual(typeof(long), Enum.GetUnderlyingType(big));
        Assert.IsNotNull(big.GetCustomAttributes(typeof(FlagsAttribute), false).SingleOrDefault());
        Assert.AreEqual("A, B", Run(session, "ldc.i8 3", "box Big")!.ToString());
    }

    /// <summary>
    /// An enum is used as a field type and compared as its integer.
    /// </summary>
    [TestMethod]
    public void Enum_AsFieldAndInteger()
    {
        var session = Load([.. Color, ".class public Pixel {", ".field public valuetype Color C", "}"]);
        Assert.AreEqual(2, Run(session, ".locals init (class Pixel p)", "ldc.i4 2", "box Color", "unbox.any Color", "ldc.i4 0", "add"));
        Assert.AreEqual("Color", session.Types[1].RuntimeType!.GetField("C")!.FieldType.Name);
    }
}
