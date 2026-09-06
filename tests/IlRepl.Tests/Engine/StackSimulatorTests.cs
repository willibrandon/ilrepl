using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="StackSimulator"/>.
/// </summary>
[TestClass]
public sealed class StackSimulatorTests
{
    private static readonly ParseContext Context = new([], [], GenericContext.Empty, new TypeResolver());

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
}
