using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="StackSimulator"/>.
/// </summary>
[TestClass]
public sealed class StackSimulatorTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver(), []);

    private static StackSimulator Run(params string[] lines)
    {
        var simulator = new StackSimulator();
        foreach (var line in lines)
        {
            simulator.Apply(InstructionParser.Parse(line, Context), Context);
        }

        return simulator;
    }

    /// <summary>
    /// Binary arithmetic promotes to the wider operand type.
    /// </summary>
    [TestMethod]
    public void Apply_Arithmetic_PromotesTypes()
    {
        Assert.AreEqual("[int32]", Run("ldc.i4 1", "ldc.i4 2", "add").Render());
        Assert.AreEqual("[int64]", Run("ldc.i8 1", "ldc.i8 2", "mul").Render());
        Assert.AreEqual("[float64]", Run("ldc.r8 1", "ldc.r8 2", "div").Render());
    }

    /// <summary>
    /// Calls pop their arguments and push their return type.
    /// </summary>
    [TestMethod]
    public void Apply_Call_PopsArgumentsPushesReturn()
    {
        var simulator = Run("ldc.i4 1", "ldc.i4 2", "call int32 Math::Max(int32, int32)");
        Assert.AreEqual("[int32]", simulator.Render());
        Assert.AreEqual("[]", Run("ldstr \"x\"", "call void Console::WriteLine(string)").Render());
    }

    /// <summary>
    /// Instance calls also pop the receiver.
    /// </summary>
    [TestMethod]
    public void Apply_InstanceCall_PopsReceiver()
    {
        Assert.AreEqual("[int32]", Run("ldstr \"abc\"", "callvirt instance int32 String::get_Length()").Render());
    }

    /// <summary>
    /// newobj pushes the constructed type, and known pushes carry their types.
    /// </summary>
    [TestMethod]
    public void Apply_KnownPushes_HaveTypes()
    {
        Assert.AreEqual("[StringBuilder]", Run("newobj instance void StringBuilder::.ctor()").Render());
        Assert.AreEqual("[null]", Run("ldnull").Render());
        Assert.AreEqual("[int32[]]", Run("ldc.i4 3", "newarr int32").Render());
        Assert.AreEqual("[object]", Run("ldc.i4 3", "box int32").Render());
        Assert.AreEqual("[int32, int32] ", Run("ldc.i4 3", "dup").Render() + " ");
    }

    /// <summary>
    /// Underflow is reported before the stack changes.
    /// </summary>
    [TestMethod]
    public void Apply_Underflow_ThrowsWithoutMutating()
    {
        var simulator = Run("ldc.i4 1");
        var ex = Assert.ThrowsExactly<ReplException>(() => simulator.Apply(InstructionParser.Parse("add", Context), Context));
        Assert.Contains("underflow", ex.Message);
        Assert.AreEqual("[int32]", simulator.Render());
    }

    /// <summary>
    /// Block boundaries reset the stack as the runtime does.
    /// </summary>
    [TestMethod]
    public void ApplyBlock_Handlers_SetExpectedStack()
    {
        var simulator = Run("ldc.i4 1");
        simulator.ApplyBlock(BlockKind.Catch, typeof(InvalidOperationException));
        Assert.AreEqual("[InvalidOperationException]", simulator.Render());
        simulator.ApplyBlock(BlockKind.Finally, null);
        Assert.AreEqual("[]", simulator.Render());
        simulator.ApplyBlock(BlockKind.Filter, null);
        Assert.AreEqual("[object]", simulator.Render());
    }

    /// <summary>
    /// Throwing and leaving empty the stack on that path.
    /// </summary>
    [TestMethod]
    public void Apply_ThrowAndLeave_ClearStack()
    {
        Assert.AreEqual("[]", Run("ldc.i4 1", "ldnull", "throw").Render());
        Assert.AreEqual("[]", Run("ldc.i4 1", "leave X").Render());
    }

    private static readonly ParseContext WithMethods = new([], [], GenericContext.Empty, new TypeResolver(),
    [
        new MethodSignature("Fib", typeof(int), [new ArgumentDeclaration(typeof(int), "n", null, "")]),
        new MethodSignature("Hi", typeof(void), []),
    ]);

    private static StackSimulator RunWithMethods(params string[] lines)
    {
        var simulator = new StackSimulator();
        foreach (var line in lines)
        {
            simulator.Apply(InstructionParser.Parse(line, WithMethods), WithMethods);
        }

        return simulator;
    }

    /// <summary>
    /// A call to a session method pops its parameters and pushes its return type.
    /// </summary>
    [TestMethod]
    public void Apply_CallSessionMethod_PopsArgumentsPushesReturn()
    {
        Assert.AreEqual("[int32]", RunWithMethods("ldc.i4 1", "call int32 Fib(int32)").Render());
        Assert.Contains("underflow", Assert.ThrowsExactly<ReplException>(() => RunWithMethods("call int32 Fib(int32)")).Message);
    }

    /// <summary>
    /// A void session method pushes nothing.
    /// </summary>
    [TestMethod]
    public void Apply_CallVoidSessionMethod_PushesNothing()
    {
        Assert.AreEqual("[]", RunWithMethods("call void Hi()").Render());
    }

    /// <summary>
    /// ldftn of a session method is a native int.
    /// </summary>
    [TestMethod]
    public void Apply_LdftnSessionMethod_PushesNativeInt()
    {
        Assert.AreEqual(typeof(nint), RunWithMethods("ldftn int32 Fib(int32)").Top);
    }

    /// <summary>
    /// ldtoken of a session method is a method handle.
    /// </summary>
    [TestMethod]
    public void Apply_LdtokenSessionMethod_PushesMethodHandle()
    {
        Assert.AreEqual(typeof(RuntimeMethodHandle), RunWithMethods("ldtoken method int32 Fib(int32)").Top);
    }

    /// <summary>
    /// ldind.ref through a managed byref preserves its reference element type.
    /// </summary>
    [TestMethod]
    public void Apply_LdindRefOnByRef_PushesElementType()
    {
        var locals = new ParseContext([new LocalDeclaration(typeof(string), "s", false)], [], GenericContext.Empty, new TypeResolver(), []);
        var simulator = new StackSimulator();
        simulator.Apply(InstructionParser.Parse("ldloca s", locals), locals);
        simulator.Apply(InstructionParser.Parse("ldind.ref", locals), locals);
        Assert.AreEqual("[string]", simulator.Render());
    }

    /// <summary>
    /// box remembers what it boxed, boxing a reference is the identity, and ldelem.ref yields the element type.
    /// </summary>
    [TestMethod]
    public void Apply_BoxAndLdelemRef_KeepTypes()
    {
        Assert.AreEqual(typeof(Boxed<int>), Run("ldc.i4 3", "box int32").Top);
        Assert.AreEqual(typeof(string), Run("ldstr \"s\"", "box string").Top);
        Assert.AreEqual(typeof(object), Run("newobj instance void Object::.ctor()", "box object").Top);
        Assert.AreEqual(typeof(Boxed<int>), Run("ldc.i4 3", "box valuetype Nullable`1<int32>").Top);
        Assert.AreEqual(typeof(string), Run("ldc.i4 1", "newarr string", "ldc.i4 0", "ldelem.ref").Top);
        Assert.AreEqual("[object]", Run("ldc.i4 3", "box int32").Render());
    }

    /// <summary>
    /// Under instance explicit the receiver is the first parameter and is popped once.
    /// </summary>
    [TestMethod]
    public void Apply_CalliExplicitThis_PopsReceiverOnce()
    {
        Assert.AreEqual("[int32]", Run("ldnull", "ldc.i4.0", "conv.i", "calli instance explicit int32(object)").Render());
        Assert.AreEqual("[int32]", Run("ldnull", "ldc.i4.0", "conv.i", "calli instance int32()").Render());
    }
}
