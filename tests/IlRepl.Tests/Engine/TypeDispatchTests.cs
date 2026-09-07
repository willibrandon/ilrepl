using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Virtual dispatch on session types: overrides, abstract members, interfaces implemented
/// implicitly and explicitly, default interface bodies, static abstract members, and newslot.
/// </summary>
[TestClass]
public sealed class TypeDispatchTests
{
    private const string ObjectCtor = "call instance void [System.Runtime]System.Object::.ctor()";

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
    /// callvirt through a base reference reaches the override; call reaches the base body.
    /// </summary>
    [TestMethod]
    public void Override_DispatchesThroughBaseReference()
    {
        var session = Load(
            ".class public Shape {",
            $".method public instance void .ctor() {{ ldarg.0; {ObjectCtor}; ret }}",
            ".method public virtual instance int32 Sides() { ldc.i4 0; ret }",
            "}",
            ".class public Tri extends Shape {",
            $".method public instance void .ctor() {{ ldarg.0; call instance void Shape::.ctor(); ret }}",
            ".method public virtual instance int32 Sides() { ldc.i4 3; ret }",
            "}");
        Assert.AreEqual(3, Run(session, "newobj instance void Tri::.ctor()", "callvirt instance int32 Shape::Sides()"));
        Assert.AreEqual(0, Run(session, "newobj instance void Tri::.ctor()", "call instance int32 Shape::Sides()"));
        Assert.AreEqual(3, Run(session, "newobj instance void Tri::.ctor()", "callvirt instance int32 Tri::Sides()"));
    }

    /// <summary>
    /// An abstract member is implemented by a derived class and dispatched from the base.
    /// </summary>
    [TestMethod]
    public void Abstract_ImplementedByDerived()
    {
        var session = Load(
            ".class public abstract Shape {",
            $".method family instance void .ctor() {{ ldarg.0; {ObjectCtor}; ret }}",
            ".method public abstract virtual instance int32 Sides() { }",
            ".method public instance int32 Doubled() { ldarg.0; callvirt instance int32 Shape::Sides(); ldc.i4 2; mul; ret }",
            "}",
            ".class public Quad extends Shape {",
            ".method public instance void .ctor() { ldarg.0; call instance void Shape::.ctor(); ret }",
            ".method public virtual instance int32 Sides() { ldc.i4 4; ret }",
            "}");
        Assert.AreEqual(8, Run(session, "newobj instance void Quad::.ctor()", "call instance int32 Shape::Doubled()"));
        Assert.IsTrue(session.Types[0].RuntimeType!.IsAbstract);
    }

    /// <summary>
    /// An interface is implemented by name, by an explicit override under another name, by a
    /// default body, and dispatched through the interface.
    /// </summary>
    [TestMethod]
    public void Interface_ImplicitExplicitAndDefault()
    {
        var session = Load(
            ".class interface public abstract IArea {",
            ".method public abstract virtual instance int32 Area() { }",
            ".method public abstract virtual instance int32 Perimeter() { }",
            ".method public virtual instance int32 Both() { ldarg.0; callvirt instance int32 IArea::Area(); ldarg.0; callvirt instance int32 IArea::Perimeter(); add; ret }",
            "}",
            ".class public Square implements IArea {",
            $".method public instance void .ctor() {{ ldarg.0; {ObjectCtor}; ret }}",
            ".method public virtual instance int32 Area() { ldc.i4 9; ret }",
            ".method private virtual instance int32 Around() { .override IArea::Perimeter; ldc.i4 12; ret }",
            "}");
        Assert.AreEqual(9, Run(session, "newobj instance void Square::.ctor()", "callvirt instance int32 IArea::Area()"));
        Assert.AreEqual(12, Run(session, "newobj instance void Square::.ctor()", "callvirt instance int32 IArea::Perimeter()"));
        Assert.AreEqual(21, Run(session, "newobj instance void Square::.ctor()", "callvirt instance int32 IArea::Both()"), "a default interface body dispatches on the implementation");
        var square = session.Types[1].RuntimeType!;
        var map = square.GetInterfaceMap(session.Types[0].RuntimeType!);
        var perimeter = Array.IndexOf(map.InterfaceMethods, map.InterfaceMethods.First(m => m.Name == "Perimeter"));
        Assert.AreEqual("Around", map.TargetMethods[perimeter].Name);
    }

    /// <summary>
    /// A static abstract interface member is implemented by a static method and called constrained.
    /// </summary>
    [TestMethod]
    public void StaticAbstract_ImplementedAndCalled()
    {
        var session = Load(
            ".class interface public abstract IZero {",
            ".method public static abstract virtual int32 Zero() { }",
            ".method public static virtual int32 One() { ldc.i4 1; ret }",
            "}",
            ".class public Num implements IZero {",
            ".method public static int32 Zero() { ldc.i4 0; ret }",
            "}");
        var num = session.Types[1].RuntimeType!;
        var map = num.GetInterfaceMap(session.Types[0].RuntimeType!);
        Assert.AreEqual("Zero", map.TargetMethods[Array.IndexOf(map.InterfaceMethods, map.InterfaceMethods.First(m => m.Name == "Zero"))].Name);
        Assert.AreEqual(0, Run(session, "constrained. Num", "call int32 IZero::Zero()"));
        Assert.AreEqual(1, Run(session, "constrained. Num", "call int32 IZero::One()"), "a static virtual default body");
    }

    /// <summary>
    /// A newslot virtual hides the base slot: the base reference still dispatches to the base.
    /// </summary>
    [TestMethod]
    public void NewSlot_HidesInsteadOfOverriding()
    {
        var session = Load(
            ".class public Shape {",
            $".method public instance void .ctor() {{ ldarg.0; {ObjectCtor}; ret }}",
            ".method public virtual instance int32 Sides() { ldc.i4 0; ret }",
            "}",
            ".class public Odd extends Shape {",
            ".method public instance void .ctor() { ldarg.0; call instance void Shape::.ctor(); ret }",
            ".method public virtual newslot instance int32 Sides() { ldc.i4 5; ret }",
            "}");
        Assert.AreEqual(0, Run(session, "newobj instance void Odd::.ctor()", "callvirt instance int32 Shape::Sides()"));
        Assert.AreEqual(5, Run(session, "newobj instance void Odd::.ctor()", "callvirt instance int32 Odd::Sides()"));
    }

    /// <summary>
    /// A struct overrides ToString and is boxed to call it.
    /// </summary>
    [TestMethod]
    public void Struct_OverridesToString()
    {
        var session = Load(
            ".class public sequential sealed Tag extends [System.Runtime]System.ValueType {",
            ".field public int32 V",
            ".method public virtual instance string ToString() { ldstr \"tag\"; ret }",
            "}");
        Assert.AreEqual("tag", Run(session, ".locals init (valuetype Tag t)", "ldloc t", "box Tag", "callvirt instance string [System.Runtime]System.Object::ToString()"));
        Assert.AreEqual("tag", Run(session, "ldloc t", "box Tag")!.ToString());
        Assert.AreEqual(typeof(object).GetMethod("ToString")!.Name, session.Types[0].RuntimeType!.GetMethod("ToString")!.GetBaseDefinition().Name);
        Assert.AreEqual(typeof(object), session.Types[0].RuntimeType!.GetMethod("ToString")!.GetBaseDefinition().DeclaringType);
    }
}
