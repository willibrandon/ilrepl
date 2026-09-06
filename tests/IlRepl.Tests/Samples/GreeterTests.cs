using IlRepl.Engine;

namespace IlRepl.Tests.Samples;

/// <summary>
/// Cells that load the real Greeter assembly and call into every member shape it offers.
/// </summary>
[TestClass]
public sealed class GreeterTests
{
    private static Session NewSession()
    {
        var session = new Session();
        session.Resolver.Load(SampleHost.Samples.GreeterDll);
        return session;
    }

    private static object? Run(Session session, params string[] lines)
    {
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        return session.Run().Value;
    }

    /// <summary>
    /// Static methods, static fields, and overload resolution by parameter types.
    /// </summary>
    [TestMethod]
    public void Statics_CallsAndFields()
    {
        var session = NewSession();
        Assert.AreEqual("Hello, IL!", Run(session, "ldstr \"IL\"", "call string Greeter.Hello::Say(string)"));
        Assert.AreEqual(5, Run(session, "ldc.i4 2", "ldc.i4 3", "call int32 Greeter.Hello::Add(int32, int32)"));
        Assert.AreEqual(5L, Run(session, "ldc.i8 2", "ldc.i8 3", "call int64 [Greeter]Greeter.Hello::Add(int64, int64)"));
        Assert.AreEqual("Hello, ", Run(session, "ldsfld string Greeter.Hello::Prefix"));
        Assert.AreEqual(1, Run(session, "ldc.i4 1", "stsfld int32 Greeter.Hello::Calls", "ldsfld int32 Greeter.Hello::Calls"));
    }

    /// <summary>
    /// Constructors, instance fields, properties, chained instance calls, and virtual calls.
    /// </summary>
    [TestMethod]
    public void Instances_ConstructFieldsAndCalls()
    {
        var session = NewSession();
        var text = Run(session,
            "ldc.i4 5",
            "newobj instance void Greeter.Counter::.ctor(int32)",
            "dup",
            "callvirt instance void Greeter.Counter::Increment()",
            "ldc.i4 10",
            "callvirt instance class Greeter.Counter Greeter.Counter::Add(int32)",
            "callvirt instance string Object::ToString()");
        Assert.AreEqual("Counter(16)", text);

        Assert.AreEqual(3, Run(session, "newobj instance void Greeter.Counter::.ctor()", "dup", "ldc.i4 3", "stfld int32 Greeter.Counter::Count", "call instance int32 Greeter.Counter::get_Value()"));
    }

    /// <summary>
    /// Value types through initobj, field stores by address, and constrained calls.
    /// </summary>
    [TestMethod]
    public void Structs_FieldsAndConstrainedCalls()
    {
        var session = NewSession();
        Assert.AreEqual(7, Run(session,
            ".locals init (valuetype Greeter.Point p)",
            "ldloca p",
            "ldc.i4 3",
            "ldc.i4 -4",
            "call instance void Greeter.Point::.ctor(int32, int32)",
            "ldloca p",
            "call instance int32 Greeter.Point::Manhattan()"));

        var area = Run(session,
            ".locals init (valuetype Greeter.Circle c)",
            "ldloca c",
            "ldc.r8 1",
            "call instance void Greeter.Circle::.ctor(float64)",
            "ldloca c",
            "constrained. Greeter.Circle",
            "callvirt instance float64 Greeter.IShape::Area()");
        Assert.AreEqual(Math.PI, (double)area!, 1e-9);
    }

    /// <summary>
    /// Interface dispatch and a class that implements it.
    /// </summary>
    [TestMethod]
    public void Interfaces_DispatchThroughCallvirt()
    {
        var session = NewSession();
        Assert.AreEqual(9.0, Run(session, "ldc.r8 3", "newobj instance void Greeter.Square::.ctor(float64)", "callvirt instance float64 Greeter.IShape::Area()"));
    }

    /// <summary>
    /// Nested types with the slash syntax.
    /// </summary>
    [TestMethod]
    public void NestedTypes_ResolveWithSlash()
    {
        var session = NewSession();
        Assert.AreEqual("inner", Run(session, "call string Greeter.Outer/Inner::get_Name()"));
    }

    /// <summary>
    /// Delegates built with ldftn and newobj, then invoked through a helper.
    /// </summary>
    [TestMethod]
    public void Delegates_LdftnAndNewobj()
    {
        var session = NewSession();
        var value = Run(session,
            "ldnull",
            "ldftn int32 Greeter.Ops::Multiply(int32, int32)",
            "newobj instance void Greeter.IntOp::.ctor(object, native int)",
            "ldc.i4 6",
            "ldc.i4 7",
            "call int32 Greeter.Ops::Apply(class Greeter.IntOp, int32, int32)");
        Assert.AreEqual(42, value);
    }

    /// <summary>
    /// A user generic type with !0 in member references and a generic method on it.
    /// </summary>
    [TestMethod]
    public void Generics_UserTypeAndMethod()
    {
        var session = NewSession();
        Assert.AreEqual(5, Run(session,
            "ldc.i4 5",
            "newobj instance void class Greeter.Box`1<int32>::.ctor(!0)",
            "callvirt instance !0 class Greeter.Box`1<int32>::Get()"));
        Assert.AreEqual("echoed", Run(session, "ldstr \"echoed\"", "call !!0 Greeter.Hello::Echo<string>(!!0)"));
    }

    /// <summary>
    /// Exceptions thrown by sample code are caught by a cell handler.
    /// </summary>
    [TestMethod]
    public void Exceptions_FromSampleAreCaught()
    {
        var session = NewSession();
        var message = Run(session,
            ".locals init (string m)",
            ".try {",
            "call void Greeter.Thrower::Boom()",
            "leave END",
            "} catch InvalidOperationException {",
            "callvirt instance string Exception::get_Message()",
            "stloc m",
            "leave END",
            "}",
            "END: ldloc m");
        Assert.AreEqual("boom", message);
    }

    /// <summary>
    /// Pointers and byrefs passed to sample methods.
    /// </summary>
    [TestMethod]
    public void Pointers_AndByrefs()
    {
        var session = NewSession();
        Assert.AreEqual(11, Run(session, ".locals init (int32 v)", "ldc.i4 11", "stloc v", "ldloca v", "conv.u", "call int32 Greeter.Hello::Deref(int32*)"));
        Assert.AreEqual(12, Run(session, "ldloca v", "ldc.i4 12", "call void Greeter.Hello::Set(int32&, int32)", "ldloc v"));
    }

    /// <summary>
    /// Arrays passed to a params method.
    /// </summary>
    [TestMethod]
    public void Arrays_ToParamsMethod()
    {
        var session = NewSession();
        var sum = Run(session, "ldc.i4 2", "newarr int32", "dup", "ldc.i4 0", "ldc.i4 20", "stelem.i4", "dup", "ldc.i4 1", "ldc.i4 22", "stelem.i4", "call int64 Greeter.Hello::Sum(int32[])");
        Assert.AreEqual(42L, sum);
    }

    /// <summary>
    /// Vararg call sites reach a __arglist method. The runtime only supports this on Windows.
    /// </summary>
    [TestMethod]
    public void Varargs_CallSite()
    {
        var session = NewSession();
        session.AddLine("ldc.i4 1");
        session.AddLine("ldstr \"two\"");
        session.AddLine("call vararg int32 Greeter.Hello::CountArgs(..., int32, string)");
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(2, session.Run().Value);
            return;
        }

        var ex = Assert.ThrowsExactly<ReplException>(() => session.Run());
        Assert.Contains("Windows", ex.Message);
    }

    /// <summary>
    /// ldtoken for a method and a field in the sample.
    /// </summary>
    [TestMethod]
    public void Tokens_MethodAndField()
    {
        var session = NewSession();
        var name = Run(session,
            "ldtoken method string Greeter.Hello::Say(string)",
            "call class [System.Runtime]System.Reflection.MethodBase [System.Runtime]System.Reflection.MethodBase::GetMethodFromHandle(valuetype [System.Runtime]System.RuntimeMethodHandle)",
            "callvirt instance string [System.Runtime]System.Reflection.MemberInfo::get_Name()");
        Assert.AreEqual("Say", name);
    }

    /// <summary>
    /// ldtoken of a vararg method takes the plain token path, not the vararg call-site one.
    /// </summary>
    [TestMethod]
    public void Tokens_VarargMethod()
    {
        var session = new Session();
        session.Resolver.Load(SampleHost.Samples.GreeterDll);
        Assert.AreEqual("CountArgs", Run(session,
            "ldtoken method vararg int32 Greeter.Hello::CountArgs()",
            "call class [System.Runtime]System.Reflection.MethodBase [System.Runtime]System.Reflection.MethodBase::GetMethodFromHandle(valuetype [System.Runtime]System.RuntimeMethodHandle)",
            "callvirt instance string [System.Runtime]System.Reflection.MemberInfo::get_Name()"));
    }
}
