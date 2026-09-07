using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for the checks a family passes when its outermost block closes: interfaces and
/// abstract members implemented, overrides in the hierarchy, kinds and layouts consistent.
/// </summary>
[TestClass]
public sealed class TypeDeclarationValidatorTests
{
    private static readonly string[] IArea =
    [
        ".class interface public abstract IArea {",
        ".method public abstract virtual instance float64 Area() { }",
        "}",
    ];

    private static readonly string[] AbstractShape =
    [
        ".class public abstract Shape {",
        ".method public abstract virtual instance int32 Sides() { }",
        "}",
    ];

    private static readonly string[] IBox =
    [
        ".class interface public abstract IBox`1<T> {",
        ".method public abstract virtual instance !0 Get() { }",
        "}",
    ];

    private static Session Load(params string[] lines) => IlLines.Load(lines);

    private static string CloseRefused(Session session) => Assert.ThrowsExactly<ReplException>(() => session.AddLine("}")).Message;

    /// <summary>
    /// An interface member is implemented by a virtual method with its name and signature, or
    /// by an override, or by the base, or by its own default body; otherwise the close is refused.
    /// </summary>
    [TestMethod]
    public void Interfaces_MustBeImplemented()
    {
        var missing = Load([.. IArea, ".class public Circle implements IArea {"]);
        Assert.Contains("class Circle must implement instance float64 IArea::Area() (declare a public virtual method", CloseRefused(missing));
        var notVirtual = Load([.. IArea, ".class public Circle implements IArea {", ".method public instance float64 Area() { ldc.r8 1; ret }"]);
        Assert.Contains("Area matches but is not virtual; add virtual to its header", CloseRefused(notVirtual));
        var implicitly = Load([.. IArea, ".class public Circle implements IArea {", ".method public virtual instance float64 Area() { ldc.r8 1; ret }"]);
        Assert.AreEqual("end of class Circle", implicitly.AddLine("}").Message);
        var explicitly = Load([.. IArea, ".class public Circle implements IArea {", ".method private virtual instance float64 Size() { .override IArea::Area; ldc.r8 1; ret }"]);
        Assert.AreEqual("end of class Circle", explicitly.AddLine("}").Message);
        var classLevel = Load([.. IArea, ".class public Circle implements IArea {",
            ".override method instance float64 IArea::Area() with method instance float64 Circle::Size()",
            ".method private virtual instance float64 Size() { ldc.r8 1; ret }"]);
        Assert.AreEqual("end of class Circle", classLevel.AddLine("}").Message);
        var inherited = Load([.. IArea,
            ".class public Shape {", ".method public virtual instance float64 Area() { ldc.r8 1; ret }", "}",
            ".class public Circle extends Shape implements IArea {"]);
        Assert.AreEqual("end of class Circle", inherited.AddLine("}").Message);
        var defaulted = Load(".class interface public abstract IArea {", ".method public virtual instance float64 Area() { ldc.r8 1; ret }", "}", ".class public Circle implements IArea {");
        Assert.AreEqual("end of class Circle", defaulted.AddLine("}").Message);
        var framework = Load(".class public Named implements [System.Runtime]System.IDisposable {");
        Assert.Contains("must implement instance void IDisposable::Dispose()", CloseRefused(framework));
    }

    /// <summary>
    /// A static abstract member is implemented by a static method of the same shape, which the
    /// validator records as an override for the writer.
    /// </summary>
    [TestMethod]
    public void StaticAbstract_ImplementedByAStaticMethod()
    {
        var session = Load(".class interface public abstract IZero {", ".method public static abstract virtual int32 Zero() { }", "}", ".class public Num implements IZero {");
        Assert.Contains("must implement static int32 IZero::Zero() (declare a static method with that name and signature)", CloseRefused(session));
        foreach (var line in IlLines.Expand(".method public static int32 Zero() { ldc.i4 0; ret }"))
        {
            session.AddLine(line);
        }

        Assert.AreEqual("end of class Num", session.AddLine("}").Message);
        var num = session.Types[^1].Declaration;
        Assert.HasCount(1, num.Overrides);
        Assert.AreEqual("Zero", num.Overrides[0].BodyName);
        Assert.IsTrue(num.Overrides[0].BodyIsStatic);
        Assert.AreEqual("", num.Overrides[0].Source, "an implied override is not a typed line");
    }

    /// <summary>
    /// A non-abstract class implements every abstract method of its bases, session or framework.
    /// </summary>
    [TestMethod]
    public void AbstractMembers_MustBeImplementedUnlessAbstract()
    {
        var session = Load([.. AbstractShape, ".class public Tri extends Shape {"]);
        Assert.Contains("class Tri must implement abstract instance int32 Shape::Sides() (declare a virtual method with that name and signature, or mark the class abstract)", CloseRefused(session));
        foreach (var line in IlLines.Expand(".method public virtual instance int32 Sides() { ldc.i4 3; ret }"))
        {
            session.AddLine(line);
        }

        Assert.AreEqual("end of class Tri", session.AddLine("}").Message);
        var stillAbstract = Load([.. AbstractShape, ".class public abstract Poly extends Shape {"]);
        Assert.AreEqual("end of class Poly", stillAbstract.AddLine("}").Message);
        var newslot = Load([.. AbstractShape, ".class public Tri extends Shape {", ".method public virtual newslot instance int32 Sides() { ldc.i4 3; ret }"]);
        Assert.Contains("must implement abstract", CloseRefused(newslot), "a newslot method hides instead of implementing");
        var twoLevels = Load([.. AbstractShape,
            ".class public abstract Poly extends Shape {", ".method public virtual instance int32 Sides() { ldc.i4 4; ret }", "}",
            ".class public Quad extends Poly {"]);
        Assert.AreEqual("end of class Quad", twoLevels.AddLine("}").Message);
        var framework = Load(".class public Custom extends [System.Runtime]System.IO.Stream {");
        Assert.Contains("class Custom must implement abstract", CloseRefused(framework));
    }

    /// <summary>
    /// An override names a slot of a base or an implemented interface.
    /// </summary>
    [TestMethod]
    public void Overrides_NameSlotsInTheHierarchy()
    {
        var session = Load([.. IArea, ".class public Circle {", ".method private virtual instance float64 Size() { .override IArea::Area; ldc.r8 1; ret }"]);
        Assert.Contains("class Circle cannot .override IArea::Area: IArea is not one of its bases or interfaces (add implements IArea)", CloseRefused(session));
    }

    /// <summary>
    /// A sealed session type cannot be extended: an accepted one is refused at the header like
    /// any loaded type, and one still being written is refused when the family closes.
    /// </summary>
    [TestMethod]
    public void BaseChain_IsChecked()
    {
        Assert.Contains("cannot extend sealed type Final", Assert.ThrowsExactly<ReplException>(() => Load(".class public sealed Final { }", ".class public More extends Final {")).Message);
        Assert.Contains("cannot extend sealed type Color", Assert.ThrowsExactly<ReplException>(() => Load(".class public Color extends [System.Runtime]System.Enum {", ".field public specialname rtspecialname int32 value__", "}", ".class public Shade extends Color {")).Message);
        var nested = Load(".class public Outer {", ".class nested public sealed Final { }", ".class nested public More extends Outer/Final { }");
        Assert.Contains("class Outer/More cannot extend sealed class Outer/Final", CloseRefused(nested));
    }

    /// <summary>
    /// Explicit layout needs an offset on every instance field.
    /// </summary>
    [TestMethod]
    public void ExplicitLayout_NeedsOffsets()
    {
        var session = Load(".class public explicit Union extends [System.Runtime]System.ValueType {", ".field [0] public int32 I", ".field public float32 F");
        Assert.Contains("struct Union has explicit layout, so every instance field needs an offset; F has none", CloseRefused(session));
        var fine = Load(".class public explicit Union extends [System.Runtime]System.ValueType {", ".field [0] public int32 I", ".field [0] public float32 F", ".field public static int32 Count");
        Assert.AreEqual("end of struct Union", fine.AddLine("}").Message);
    }

    /// <summary>
    /// An enum needs its value__ field.
    /// </summary>
    [TestMethod]
    public void KindRules_AreChecked()
    {
        var noValue = Load(".class public enum Color {");
        Assert.Contains("enum Color needs exactly one value__ field", CloseRefused(noValue));
    }

    /// <summary>
    /// Generic parameter constraints cannot contradict each other.
    /// </summary>
    [TestMethod]
    public void Constraints_AreChecked()
    {
        Assert.Contains("cannot be both class and valuetype", Assert.ThrowsExactly<ReplException>(() => Load(".class public Box`1<class valuetype T> {")).Message);
        var twoClasses = Load(".class public Box`1<([System.Runtime]System.Exception, [System.Runtime]System.Attribute) T> {");
        Assert.Contains("has more than one class constraint", CloseRefused(twoClasses));
    }

    /// <summary>
    /// A generic interface instantiated with the type's own parameter is matched after substitution.
    /// </summary>
    [TestMethod]
    public void GenericInterface_MatchedAfterSubstitution()
    {
        var session = Load([.. IBox, ".class public Box`1<T> implements class IBox`1<!0> {", ".field public !0 V"]);
        Assert.Contains("must implement instance !T IBox<!T>::Get()", CloseRefused(session));
        foreach (var line in IlLines.Expand(".method public virtual instance !0 Get() { ldarg.0; ldfld !0 class Box`1<!0>::V; ret }"))
        {
            session.AddLine(line);
        }

        Assert.AreEqual("end of class Box`1", session.AddLine("}").Message);
        var closed = Load([.. IBox, ".class public IntBox implements class IBox`1<int32> {", ".method public virtual instance int32 Get() { ldc.i4 1; ret }"]);
        Assert.AreEqual("end of class IntBox", closed.AddLine("}").Message);
        var wrong = Load([.. IBox, ".class public IntBox implements class IBox`1<int32> {", ".method public virtual instance string Get() { ldnull; ret }"]);
        Assert.Contains("must implement instance int32 IBox<int32>::Get()", CloseRefused(wrong));
    }

    /// <summary>
    /// A generic interface method is implemented by a generic method with the same shape; the
    /// two methods' own parameters match by position.
    /// </summary>
    [TestMethod]
    public void GenericInterfaceMethod_MatchedByPosition()
    {
        var session = Load(".class interface public abstract IFoo {", ".method public abstract virtual instance !!0 Id<T>(!!0 v) { }", "}",
            ".class public Foo implements IFoo {", ".method public virtual instance !!0 Id<T>(!!0 v) { ldarg v; ret }");
        Assert.AreEqual("end of class Foo", session.AddLine("}").Message);
        var wrong = Load(".class interface public abstract IFoo {", ".method public abstract virtual instance !!0 Id<T>(!!0 v) { }", "}",
            ".class public Foo implements IFoo {", ".method public virtual instance !!0 Id<T, U>(!!0 v) { ldarg v; ret }");
        Assert.Contains("must implement", CloseRefused(wrong));
    }
}
