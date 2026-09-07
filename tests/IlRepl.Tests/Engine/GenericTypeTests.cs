using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Generic session types: instantiation from cells, per-closed-type statics, constraints,
/// variance, and generic methods on them.
/// </summary>
[TestClass]
public sealed class GenericTypeTests
{
    private static readonly string[] Box =
    [
        ".class public Box`1<T> {",
        ".field public !0 Value",
        ".field public static int32 Made",
        ".method public instance void .ctor(!0 v) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg v; stfld !0 class Box`1<!0>::Value; ldsfld int32 class Box`1<!0>::Made; ldc.i4 1; add; stsfld int32 class Box`1<!0>::Made; ret }",
        ".method public instance !0 Get() { ldarg.0; ldfld !0 class Box`1<!0>::Value; ret }",
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
    /// A generic class is instantiated with a value type and a reference type from a cell.
    /// </summary>
    [TestMethod]
    public void Generic_InstantiatedFromCells()
    {
        var session = Load(Box);
        Assert.AreEqual(5, Run(session, "ldc.i4 5", "newobj instance void class Box`1<int32>::.ctor(!0)", "call instance !0 class Box`1<int32>::Get()"));
        Assert.AreEqual("s", Run(session, "ldstr \"s\"", "newobj instance void class Box`1<string>::.ctor(!0)", "call instance !0 class Box`1<string>::Get()"));
        var box = session.Types[0].RuntimeType!;
        Assert.IsTrue(box.IsGenericTypeDefinition);
        Assert.AreEqual("Box`1", box.Name);
    }

    /// <summary>
    /// Each closed type has its own statics.
    /// </summary>
    [TestMethod]
    public void Generic_StaticsArePerClosedType()
    {
        var session = Load(Box);
        Run(session, "ldc.i4 1", "newobj instance void class Box`1<int32>::.ctor(!0)", "pop", "ldc.i4 2", "newobj instance void class Box`1<int32>::.ctor(!0)", "pop");
        Run(session, "ldstr \"a\"", "newobj instance void class Box`1<string>::.ctor(!0)", "pop");
        Assert.AreEqual(2, Run(session, "ldsfld int32 class Box`1<int32>::Made"));
        Assert.AreEqual(1, Run(session, "ldsfld int32 class Box`1<string>::Made"));
        Assert.AreEqual(0, Run(session, "ldsfld int32 class Box`1<float64>::Made"));
    }

    /// <summary>
    /// Constraints and variance are carried to the runtime type.
    /// </summary>
    [TestMethod]
    public void Generic_ConstraintsAndVariance()
    {
        var session = Load(
            ".class interface public abstract ISource`1<+T> {", ".method public abstract virtual instance !0 Next() { }", "}",
            ".class public Sorted`1<([System.Runtime]System.IComparable`1<!0>) T> {", "}",
            ".class public Only`1<class .ctor T> {", "}");
        var source = session.Types[0].RuntimeType!.GetGenericArguments()[0];
        Assert.IsTrue(source.GenericParameterAttributes.HasFlag(System.Reflection.GenericParameterAttributes.Covariant));
        var sorted = session.Types[1].RuntimeType!.GetGenericArguments()[0];
        Assert.AreEqual(typeof(IComparable<>), sorted.GetGenericParameterConstraints()[0].GetGenericTypeDefinition());
        var only = session.Types[2].RuntimeType!.GetGenericArguments()[0];
        Assert.IsTrue(only.GenericParameterAttributes.HasFlag(System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint));
        Assert.IsTrue(only.GenericParameterAttributes.HasFlag(System.Reflection.GenericParameterAttributes.DefaultConstructorConstraint));
    }

    /// <summary>
    /// A generic method on a session type is called with its own type argument.
    /// </summary>
    [TestMethod]
    public void GenericMethod_OnASessionType()
    {
        var session = Load(
            ".class public Util {",
            ".method public static !!0 Same<T>(!!0 v) { ldarg v; ret }",
            "}");
        Assert.AreEqual(3, Run(session, "ldc.i4 3", "call !!0 Util::Same<int32>(!!0)"));
        Assert.IsTrue(session.Types[0].RuntimeType!.GetMethod("Same")!.IsGenericMethodDefinition);
    }

    /// <summary>
    /// A generic session type appears in a session method's signature and in another type's field.
    /// </summary>
    [TestMethod]
    public void Generic_UsedByOtherDefinitions()
    {
        var session = Load([.. Box,
            ".class public Holder {", ".field public static class Box`1<int32> B", "}",
            ".method class Box`1<int32> Wrap(int32 v) { ldarg v; newobj instance void class Box`1<int32>::.ctor(!0); ret }"]);
        Assert.AreEqual(4, Run(session, "ldc.i4 4", "call class Box`1<int32> Wrap(int32)", "dup", "stsfld class Box`1<int32> Holder::B", "call instance !0 class Box`1<int32>::Get()"));
        Assert.AreEqual(4, Run(session, "ldsfld class Box`1<int32> Holder::B", "ldfld !0 class Box`1<int32>::Value"));
    }

    /// <summary>
    /// A generic method of a generic type mixes !0 and !!0, and a call site names the callee's
    /// parameters with !N even inside another generic body.
    /// </summary>
    [TestMethod]
    public void GenericMethod_InAGenericType_MixesTypeAndMethodParameters()
    {
        var session = Load(
            ".class public Pair`1<T> {",
            ".field public !0 V",
            ".method public instance void .ctor(!0 v) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg v; stfld !0 class Pair`1<!0>::V; ret }",
            ".method public instance !!0 Map<U>(class [System.Runtime]System.Func`2<!0, !!0> f) { ldarg f; ldarg.0; ldfld !0 class Pair`1<!0>::V; callvirt instance !1 class [System.Runtime]System.Func`2<!0, !!0>::Invoke(!0); ret }",
            "}");
        var pair = session.Types[0].RuntimeType!;
        var map = pair.GetMethod("Map")!;
        Assert.IsTrue(map.IsGenericMethodDefinition);
        var closed = pair.MakeGenericType(typeof(int));
        var instance = Activator.CreateInstance(closed, 21)!;
        var mapped = closed.GetMethod("Map")!.MakeGenericMethod(typeof(string)).Invoke(instance, [new Func<int, string>(i => (i * 2).ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        Assert.AreEqual("42", mapped);
    }

    /// <summary>
    /// A generic virtual method is overridden and dispatched.
    /// </summary>
    [TestMethod]
    public void GenericVirtualMethod_Dispatches()
    {
        var session = Load(
            ".class public Base {",
            ".method public instance void .ctor() { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ret }",
            ".method public virtual instance string Describe<T>(!!0 v) { ldstr \"base\"; ret }",
            "}",
            ".class public Derived extends Base {",
            ".method public instance void .ctor() { ldarg.0; call instance void Base::.ctor(); ret }",
            ".method public virtual instance string Describe<T>(!!0 v) { ldstr \"derived\"; ret }",
            "}");
        Assert.AreEqual("derived", Run(session, "newobj instance void Derived::.ctor()", "ldc.i4 1", "callvirt instance string Base::Describe<int32>(!!0)"));
    }

    /// <summary>
    /// A generic type derives from a generic session base instantiated with its own parameter.
    /// </summary>
    [TestMethod]
    public void Generic_DerivesFromGenericSessionBase()
    {
        var session = Load(
            ".class public Base`1<T> {",
            ".field public !0 V",
            ".method public instance void .ctor(!0 v) { ldarg.0; call instance void [System.Runtime]System.Object::.ctor(); ldarg.0; ldarg v; stfld !0 class Base`1<!0>::V; ret }",
            "}",
            ".class public Derived`1<T> extends class Base`1<!0> {",
            ".method public instance void .ctor(!0 v) { ldarg.0; ldarg v; call instance void class Base`1<!0>::.ctor(!0); ret }",
            "}");
        Assert.AreEqual(8, Run(session, "ldc.i4 8", "newobj instance void class Derived`1<int32>::.ctor(!0)", "ldfld !0 class Base`1<int32>::V"));
        var derived = session.Types[1].RuntimeType!;
        Assert.AreEqual(session.Types[0].RuntimeType, derived.BaseType!.GetGenericTypeDefinition());
    }

    /// <summary>
    /// A generic struct holds its parameter by value.
    /// </summary>
    [TestMethod]
    public void GenericStruct_HoldsItsParameter()
    {
        var session = Load(".class public sequential sealed Opt`1<T> extends [System.Runtime]System.ValueType {", ".field public !0 Value", ".field public bool Has", "}");
        Assert.AreEqual(5, Run(session, ".locals init (valuetype Opt`1<int32> o)", "ldloca o", "ldc.i4 5", "stfld !0 valuetype Opt`1<int32>::Value", "ldloc o", "ldfld !0 valuetype Opt`1<int32>::Value"));
        Assert.IsTrue(session.Types[0].RuntimeType!.IsValueType);
    }

    /// <summary>
    /// A recursive constraint lets a constrained call reach the argument's implementation.
    /// </summary>
    [TestMethod]
    public void RecursiveConstraint_AndConstrainedCall()
    {
        var session = Load(
            ".class public Max`1<([System.Runtime]System.IComparable`1<!0>) T> {",
            ".method public static !0 Of(!0 a, !0 b) { ldarga a; ldarg b; constrained. !0; callvirt instance int32 class [System.Runtime]System.IComparable`1<!0>::CompareTo(!0); ldc.i4 0; bge L; ldarg b; ret; L: ldarg a; ret }",
            "}");
        Assert.AreEqual(7, Run(session, "ldc.i4 3", "ldc.i4 7", "call !0 class Max`1<int32>::Of(!0, !0)"));
        Assert.AreEqual("b", Run(session, "ldstr \"b\"", "ldstr \"a\"", "call !0 class Max`1<string>::Of(!0, !0)"));
    }

    /// <summary>
    /// A type initializer runs once per closed type, and ldtoken names the instantiation.
    /// </summary>
    [TestMethod]
    public void Generic_InitializerPerClosedType_AndLdtoken()
    {
        var session = Load(
            ".class public Counted`1<T> {",
            ".field public static int32 N",
            ".method static void .cctor() { ldsfld int32 class Counted`1<!0>::N; ldc.i4 5; add; stsfld int32 class Counted`1<!0>::N; ret }",
            "}");
        Assert.AreEqual(5, Run(session, "ldsfld int32 class Counted`1<string>::N"));
        Assert.AreEqual(5, Run(session, "ldsfld int32 class Counted`1<string>::N"));
        Assert.AreEqual(5, Run(session, "ldsfld int32 class Counted`1<int32>::N"));
        var token = Run(session, "ldtoken class Counted`1<int32>", "call class [System.Runtime]System.Type [System.Runtime]System.Type::GetTypeFromHandle(valuetype [System.Runtime]System.RuntimeTypeHandle)") as Type;
        Assert.IsNotNull(token);
        Assert.AreEqual(session.Types[0].RuntimeType, token.GetGenericTypeDefinition());
    }

    /// <summary>
    /// A cell with its own type parameter instantiates a session generic with it.
    /// </summary>
    [TestMethod]
    public void Generic_FromACellTypeParameter()
    {
        var session = Load(Box);
        Assert.AreEqual(0, Run(session, ".typeparams (T)", ".typeargs (int32)", ".locals init (!!0 v)", "ldloc v", "newobj instance void class Box`1<!!0>::.ctor(!0)", "ldfld !0 class Box`1<!!0>::Value", "box !!0"));
    }
}
