using IlRepl.Engine;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Tests for the display of session instances: structural by default, through readers that run
/// none of the type's code, bounded and cycle-safe, and through ToString when the type has one.
/// </summary>
[TestClass]
public sealed class SessionValueFormatterTests
{
    private const string ObjectCtor = "call instance void [System.Runtime]System.Object::.ctor()";

    private static string Text(object? value) => string.Concat(ValueFormatter.FormatWithType(value).Select(s => s.Text));

    private static object? Run(Session session, params string[] lines)
    {
        foreach (var line in IlLines.Expand(lines))
        {
            session.AddLine(line);
        }

        return session.Run().Value;
    }

    /// <summary>
    /// A struct shows its fields in declaration order, then its type.
    /// </summary>
    [TestMethod]
    public void Struct_ShowsFields()
    {
        var session = IlLines.Load(
            ".class public sequential sealed Point extends [System.Runtime]System.ValueType {",
            ".field public int32 X",
            ".field public int32 Y",
            ".field private string Tag",
            ".method public instance void .ctor(int32 x, int32 y) { ldarg.0; ldarg x; stfld int32 Point::X; ldarg.0; ldarg y; stfld int32 Point::Y; ldarg.0; ldstr \"p\"; stfld string Point::Tag; ret }",
            "}");
        var value = Run(session, "ldc.i4 3", "ldc.i4 4", "newobj instance void Point::.ctor(int32, int32)", "box Point");
        Assert.AreEqual("Point { X = 3, Y = 4, Tag = \"p\" } : Point", Text(value), "private fields show too");
        var empty = Run(session, ".locals init (valuetype Point p)", "ldloc p", "box Point");
        Assert.AreEqual("Point { X = 0, Y = 0, Tag = null } : Point", Text(empty));
    }

    /// <summary>
    /// Base fields come first, a name declared twice is qualified, and a nested session value is
    /// shown structurally to a bounded depth.
    /// </summary>
    [TestMethod]
    public void Class_BaseFirstQualifiedAndNested()
    {
        var session = IlLines.Load(
            ".class public Base {",
            ".field public int32 Id",
            $".method public instance void .ctor() {{ ldarg.0; {ObjectCtor}; ldarg.0; ldc.i4 1; stfld int32 Base::Id; ret }}",
            "}",
            ".class public Node extends Base {",
            ".field public int32 Id",
            ".field public class Node Next",
            ".method public instance void .ctor() { ldarg.0; call instance void Base::.ctor(); ldarg.0; ldc.i4 2; stfld int32 Node::Id; ret }",
            "}");
        var chain = Run(session, ".locals init (class Node a, class Node b, class Node c, class Node d)",
            "newobj instance void Node::.ctor()", "stloc a", "newobj instance void Node::.ctor()", "stloc b", "newobj instance void Node::.ctor()", "stloc c", "newobj instance void Node::.ctor()", "stloc d",
            "ldloc a", "ldloc b", "stfld class Node Node::Next", "ldloc b", "ldloc c", "stfld class Node Node::Next", "ldloc c", "ldloc d", "stfld class Node Node::Next", "ldloc a");
        Assert.AreEqual("Node { Base.Id = 1, Node.Id = 2, Next = Node { Base.Id = 1, Node.Id = 2, Next = Node { Base.Id = 1, Node.Id = 2, Next = Node {…} } } } : Node", Text(chain));
    }

    /// <summary>
    /// A cycle is marked on the path instead of followed.
    /// </summary>
    [TestMethod]
    public void Cycle_IsMarked()
    {
        var session = IlLines.Load(
            ".class public Node {",
            ".field public class Node Next",
            $".method public instance void .ctor() {{ ldarg.0; {ObjectCtor}; ret }}",
            "}");
        var self = Run(session, ".locals init (class Node n)", "newobj instance void Node::.ctor()", "stloc n", "ldloc n", "ldloc n", "stfld class Node Node::Next", "ldloc n");
        Assert.AreEqual("Node { Next = ↺ Node } : Node", Text(self));
    }

    /// <summary>
    /// A type that overrides ToString is shown through it, and a throwing override is marked
    /// rather than propagated.
    /// </summary>
    [TestMethod]
    public void ToStringOverride_IsUsed()
    {
        var session = IlLines.Load(
            ".class public Named {",
            ".field public int32 Hidden",
            $".method public instance void .ctor() {{ ldarg.0; {ObjectCtor}; ret }}",
            ".method public virtual instance string ToString() { ldstr \"named!\"; ret }",
            "}",
            ".class public Angry {",
            $".method public instance void .ctor() {{ ldarg.0; {ObjectCtor}; ret }}",
            ".method public virtual instance string ToString() { newobj instance void [System.Runtime]System.InvalidOperationException::.ctor(); throw }",
            "}",
            ".class public Quiet extends Named {",
            ".field public int32 More",
            ".method public instance void .ctor() { ldarg.0; call instance void Named::.ctor(); ret }",
            "}");
        Assert.AreEqual("named! : Named", Text(Run(session, "newobj instance void Named::.ctor()")));
        Assert.AreEqual("{ToString threw InvalidOperationException} : Angry", Text(Run(session, "newobj instance void Angry::.ctor()")));
        Assert.AreEqual("named! : Quiet", Text(Run(session, "newobj instance void Quiet::.ctor()")), "an inherited override counts");
        Assert.IsTrue(ValueFormatter.HasOwnToString(Run(session, "newobj instance void Named::.ctor()")!));
    }

    /// <summary>
    /// The display never runs the type's code: no ToString when there is none of its own, and a
    /// session type that enumerates is shown by its fields, not enumerated.
    /// </summary>
    [TestMethod]
    public void Display_RunsNoUserCode()
    {
        var session = IlLines.Load(
            ".class public Loud implements [System.Runtime]System.Collections.IEnumerable {",
            ".field public int32 Calls",
            $".method public instance void .ctor() {{ ldarg.0; {ObjectCtor}; ret }}",
            ".method public virtual instance class [System.Runtime]System.Collections.IEnumerator GetEnumerator() { ldarg.0; dup; ldfld int32 Loud::Calls; ldc.i4 1; add; stfld int32 Loud::Calls; ldnull; ret }",
            "}");
        var value = Run(session, "newobj instance void Loud::.ctor()")!;
        Assert.AreEqual("Loud { Calls = 0 } : Loud", Text(value));
        Assert.AreEqual(0, value.GetType().GetField("Calls")!.GetValue(value), "GetEnumerator did not run");
        Assert.IsFalse(ValueFormatter.HasOwnToString(value));
    }

    /// <summary>
    /// Long displays are bounded: many fields end in an ellipsis, and the text is cut at the limit.
    /// </summary>
    [TestMethod]
    public void Display_IsBounded()
    {
        var lines = new List<string> { ".class public sequential sealed Wide extends [System.Runtime]System.ValueType {" };
        for (var i = 0; i < 20; i++)
        {
            lines.Add($".field public int32 F{i}");
        }

        lines.Add("}");
        var session = IlLines.Load([.. lines]);
        var text = Text(Run(session, ".locals init (valuetype Wide w)", "ldloc w", "box Wide"));
        Assert.Contains("F15 = 0, … }", text);
        Assert.DoesNotContain("F16", text);
        var big = IlLines.Load(
            ".class public sequential sealed Big extends [System.Runtime]System.ValueType {",
            ".field public string S",
            "}");
        var longText = Text(Run(big, ".locals init (valuetype Big b)", "ldloca b", "ldstr \"" + new string('x', 700) + "\"", "stfld string Big::S", "ldloc b", "box Big"));
        Assert.EndsWith("… : Big", longText);
        Assert.IsLessThan(600, longText.Length);
    }

    /// <summary>
    /// Enums and arrays of session values display normally.
    /// </summary>
    [TestMethod]
    public void Enums_AndArrays()
    {
        var session = IlLines.Load(
            ".class public enum Color {",
            ".field public specialname rtspecialname int32 value__",
            ".field public static literal valuetype Color Red = int32(0)",
            ".field public static literal valuetype Color Green = int32(1)",
            "}",
            ".class public sequential sealed Pixel extends [System.Runtime]System.ValueType {",
            ".field public valuetype Color C",
            "}");
        Assert.AreEqual("Green : Color", Text(Run(session, "ldc.i4 1", "box Color")));
        Assert.AreEqual("Pixel { C = Red } : Pixel", Text(Run(session, ".locals init (valuetype Pixel p)", "ldloc p", "box Pixel")));
        Assert.AreEqual("[Pixel { C = Red }, Pixel { C = Red }] : Pixel[]", Text(Run(session, "ldc.i4 2", "newarr Pixel")));
    }

    /// <summary>
    /// A pointer field is shown as its address rather than treated as an object.
    /// </summary>
    [TestMethod]
    public void PointerField_ShowsTheAddress()
    {
        var session = IlLines.Load(".class public sequential sealed Raw extends [System.Runtime]System.ValueType {", ".field public int32* P", "}");
        var value = Run(session, ".locals init (valuetype Raw r)", "ldloca r", "ldc.i4 1", "conv.i", "stfld int32* Raw::P", "ldloc r", "box Raw");
        Assert.AreEqual("Raw { P = 1 } : Raw", Text(value));
    }
}
