using System.Runtime.InteropServices;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Sequential and explicit layout, .pack, .size, and offsets, as the runtime sees them and as
/// sizeof reports them.
/// </summary>
[TestClass]
public sealed class LayoutTests
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
    /// .pack and .size shape a sequential struct.
    /// </summary>
    [TestMethod]
    public void Sequential_PackAndSize()
    {
        var session = Load(
            ".class public sequential sealed Packed extends [System.Runtime]System.ValueType {",
            ".pack 1",
            ".field public uint8 A",
            ".field public int32 B",
            "}",
            ".class public sequential sealed Padded extends [System.Runtime]System.ValueType {",
            ".size 16",
            ".field public int32 A",
            "}");
        Assert.AreEqual(5, Convert.ToInt32(Run(session, "sizeof Packed"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(16, Convert.ToInt32(Run(session, "sizeof Padded"), System.Globalization.CultureInfo.InvariantCulture));
        var packed = session.Types[0].RuntimeType!;
        Assert.AreEqual(1, packed.StructLayoutAttribute!.Pack);
        Assert.AreEqual(LayoutKind.Sequential, packed.StructLayoutAttribute.Value);
        Assert.AreEqual(16, session.Types[1].RuntimeType!.StructLayoutAttribute!.Size);
    }

    /// <summary>
    /// Explicit offsets overlay fields, so a float's bits read back as an int.
    /// </summary>
    [TestMethod]
    public void Explicit_OffsetsOverlay()
    {
        var session = Load(
            ".class public explicit sealed Bits extends [System.Runtime]System.ValueType {",
            ".field [0] public float32 F",
            ".field [0] public int32 I",
            ".field [4] public int32 Tail",
            "}");
        Assert.AreEqual(8, Convert.ToInt32(Run(session, "sizeof Bits"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(0x3F800000, Run(session, ".locals init (valuetype Bits b)", "ldloca b", "ldc.r4 1.0", "stfld float32 Bits::F", "ldloca b", "ldfld int32 Bits::I"));
        var bits = session.Types[0].RuntimeType!;
        Assert.AreEqual(LayoutKind.Explicit, bits.StructLayoutAttribute!.Value);
        Assert.AreEqual(4, (int)Marshal.OffsetOf(bits, "Tail"));
    }

    /// <summary>
    /// A nested struct of one layout inside a struct of another keeps both layouts.
    /// </summary>
    [TestMethod]
    public void Nested_MixedLayouts()
    {
        var session = Load(
            ".class public sequential sealed Outer extends [System.Runtime]System.ValueType {",
            ".pack 2",
            ".class nested public explicit sealed Inner extends [System.Runtime]System.ValueType {",
            ".field [0] public int32 X",
            ".field [2] public int16 Y",
            "}",
            ".field public uint8 Tag",
            ".field public valuetype Outer/Inner In",
            "}");
        var outer = session.Types[0].RuntimeType!;
        var inner = session.Types[0].Types["Outer/Inner"];
        Assert.AreEqual(LayoutKind.Sequential, outer.StructLayoutAttribute!.Value);
        Assert.AreEqual(2, outer.StructLayoutAttribute.Pack);
        Assert.AreEqual(LayoutKind.Explicit, inner.StructLayoutAttribute!.Value);
        Assert.AreEqual(4, Convert.ToInt32(Run(session, "sizeof Outer/Inner"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(6, Convert.ToInt32(Run(session, "sizeof Outer"), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Auto layout is the default for a class and is what the runtime reports.
    /// </summary>
    [TestMethod]
    public void Auto_IsTheDefaultForClasses()
    {
        var session = Load(".class public Plain {", ".field public int32 X", "}");
        Assert.IsTrue(session.Types[0].RuntimeType!.IsAutoLayout);
    }
}
