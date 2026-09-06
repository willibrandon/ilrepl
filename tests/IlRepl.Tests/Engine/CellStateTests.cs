using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="CellState"/> in method mode: parameters, typed <c>ret</c>, and the closing brace.
/// </summary>
[TestClass]
public sealed class CellStateTests
{
    private static readonly TypeResolver Resolver = new();

    private static MethodSignature Signature(string name, Type returnType, params (Type Type, string Name)[] parameters) =>
        new(name, returnType, parameters.Select(p => new ArgumentDeclaration(p.Type, p.Name, null, "")).ToList());

    private static CellState Body(MethodSignature signature, params string[] lines)
    {
        var state = new CellState(Resolver, GenericContext.Empty, [signature], signature, braceOpen: true);
        foreach (var line in lines)
        {
            state.Apply(line);
        }

        return state;
    }

    /// <summary>
    /// The header's parameters are the ldarg targets, by name and by index.
    /// </summary>
    [TestMethod]
    public void Apply_LdargParameterByName_PushesParameterType()
    {
        var state = Body(Signature("Fib", typeof(int), (typeof(int), "n")), "ldarg n", "ldarg.0");
        Assert.AreEqual("[int32, int32]", state.Stack.Render());
        Assert.IsTrue(state.IsMethod);
        Assert.HasCount(1, state.Arguments);
    }

    /// <summary>
    /// ret with one value of the declared type is emitted inline and empties the stack.
    /// </summary>
    [TestMethod]
    public void Apply_RetWithMatchingValue_IsAccepted()
    {
        var state = Body(Signature("One", typeof(int)), "ldc.i4 1");
        var result = state.Apply("ret");
        Assert.AreEqual(LineOutcome.Instruction, result.Outcome);
        Assert.AreEqual(1, result.Instruction!.RetPops);
        Assert.IsFalse(result.Instruction.RetNull);
        Assert.IsNull(result.Instruction.RetBox);
        Assert.AreEqual("[]", state.Stack.Render());
        Assert.IsTrue(state.LastInstructionEndsFlow);
    }

    /// <summary>
    /// ret names the type it needs and the type it found.
    /// </summary>
    [TestMethod]
    public void Apply_RetWithWrongType_Explains()
    {
        var state = Body(Signature("Answer", typeof(int)), "ldstr \"42\"");
        Assert.Contains("ret needs int32 on the stack but found string", Assert.ThrowsExactly<ReplException>(() => state.Apply("ret")).Message);
        Assert.AreEqual("[string]", state.Stack.Render(), "a rejected ret changes nothing");

        var boxed = Body(Signature("Boxed", typeof(object)), "ldc.i4 1");
        Assert.Contains("(box it first)", Assert.ThrowsExactly<ReplException>(() => boxed.Apply("ret")).Message);

        var empty = Body(Signature("Empty", typeof(int)));
        Assert.Contains("but the stack is empty", Assert.ThrowsExactly<ReplException>(() => empty.Apply("ret")).Message);
    }

    /// <summary>
    /// A void method returns with an empty stack.
    /// </summary>
    [TestMethod]
    public void Apply_RetInVoidMethodWithValue_Throws()
    {
        var state = Body(Signature("Hi", typeof(void)), "ldc.i4 1");
        Assert.Contains("ret in void method Hi needs an empty stack but found [int32] (pop first)", Assert.ThrowsExactly<ReplException>(() => state.Apply("ret")).Message);
        state.Apply("pop");
        var result = state.Apply("ret");
        Assert.AreEqual(0, result.Instruction!.RetPops);
        Assert.IsFalse(result.Instruction.RetNull, "a void ret pushes nothing");
    }

    /// <summary>
    /// ret takes exactly one value.
    /// </summary>
    [TestMethod]
    public void Apply_RetWithTwoValues_Throws()
    {
        var state = Body(Signature("Two", typeof(int)), "ldc.i4 1", "ldc.i4 2");
        Assert.Contains("ret needs exactly one int32 on the stack but found [int32, int32]", Assert.ThrowsExactly<ReplException>(() => state.Apply("ret")).Message);
    }

    /// <summary>
    /// A closing brace with a compatible stack ends the method with an implied ret.
    /// </summary>
    [TestMethod]
    public void Apply_CloseWithCompatibleStack_ReturnsMethodEnd()
    {
        var state = Body(Signature("One", typeof(int)), "ldc.i4 1");
        Assert.AreEqual(LineOutcome.MethodEnd, state.Apply("}").Outcome);
        Assert.IsFalse(state.LastInstructionEndsFlow, "the ret is implied by the compiler, not recorded");

        var empty = Body(Signature("Nop", typeof(void)));
        Assert.AreEqual(LineOutcome.MethodEnd, empty.Apply("}").Outcome);
    }

    /// <summary>
    /// A closing brace with the wrong stack is refused and names the method.
    /// </summary>
    [TestMethod]
    public void Apply_CloseWithWrongStack_Throws()
    {
        var two = Body(Signature("F", typeof(int)), "ldc.i4 1", "ldc.i4 2");
        Assert.Contains("method F needs a ret before }: the stack holds [int32, int32] but F returns int32", Assert.ThrowsExactly<ReplException>(() => two.Apply("}")).Message);

        var empty = Body(Signature("F", typeof(int)));
        Assert.Contains("the stack is empty but F returns int32", Assert.ThrowsExactly<ReplException>(() => empty.Apply("}")).Message);

        var voidWithValue = Body(Signature("Nop", typeof(void)), "ldc.i4 1");
        Assert.Contains("the stack holds [int32] but Nop returns void (pop it)", Assert.ThrowsExactly<ReplException>(() => voidWithValue.Apply("}")).Message);

        var wrongType = Body(Signature("F", typeof(int)), "ldstr \"x\"");
        Assert.Contains("the stack holds [string] but F returns int32", Assert.ThrowsExactly<ReplException>(() => wrongType.Apply("}")).Message);
    }

    /// <summary>
    /// A branch to a label that was never defined blocks the close.
    /// </summary>
    [TestMethod]
    public void Apply_CloseWithPendingLabel_Throws()
    {
        var state = Body(Signature("F", typeof(void)), "br DONE");
        Assert.Contains("label referenced but never defined: DONE", Assert.ThrowsExactly<ReplException>(() => state.Apply("}")).Message);
    }

    /// <summary>
    /// After an inline ret the close needs nothing from the stack.
    /// </summary>
    [TestMethod]
    public void Apply_CloseAfterRet_NeedsNoImplicitRet()
    {
        var state = Body(Signature("One", typeof(int)), "ldc.i4 1", "ret");
        Assert.AreEqual(LineOutcome.MethodEnd, state.Apply("}").Outcome);
        Assert.IsTrue(state.LastInstructionEndsFlow);
    }

    /// <summary>
    /// Inside a protected region, a brace closes the region; the next one closes the method.
    /// </summary>
    [TestMethod]
    public void Apply_CloseInsideTry_ClosesRegionNotMethod()
    {
        var state = Body(Signature("Guarded", typeof(void)), ".try {", "nop", "} finally {", "nop");
        var region = state.Apply("}");
        Assert.AreEqual(LineOutcome.Block, region.Outcome);
        Assert.AreEqual("end of protected region", region.Message);
        Assert.AreEqual(0, state.OpenBlockDepth);
        Assert.AreEqual(LineOutcome.MethodEnd, state.Apply("}").Outcome);
    }

    /// <summary>
    /// Declarations that belong to the cell are refused inside a method.
    /// </summary>
    [TestMethod]
    public void Apply_ArgsInsideMethod_Throws()
    {
        var state = Body(Signature("F", typeof(void)));
        Assert.Contains(".args is not allowed inside a method; parameters come from the header", Assert.ThrowsExactly<ReplException>(() => state.Apply(".args (int32 x = 1)")).Message);
        Assert.Contains("cannot be vararg", Assert.ThrowsExactly<ReplException>(() => state.Apply(".vararg")).Message);
        Assert.Contains(".typeparams is not allowed inside a method", Assert.ThrowsExactly<ReplException>(() => state.Apply(".typeparams (T)")).Message);
        Assert.Contains("close the method with } first", Assert.ThrowsExactly<ReplException>(() => state.Apply(".typeargs (int32)")).Message);
        Assert.AreEqual(LineOutcome.Locals, state.Apply(".locals init (int32 x)").Outcome);
    }

    /// <summary>
    /// Methods do not nest.
    /// </summary>
    [TestMethod]
    public void Apply_NestedMethod_Throws()
    {
        var state = Body(Signature("Outer", typeof(void)));
        Assert.Contains("a method is already open (Outer); close it with } before defining another", Assert.ThrowsExactly<ReplException>(() => state.Apply(".method void Inner() {")).Message);
    }

    /// <summary>
    /// The opening brace is accepted once, on the header or the next line, and never twice.
    /// </summary>
    [TestMethod]
    public void Apply_SecondBrace_Throws()
    {
        var signature = Signature("F", typeof(void));
        var onHeader = Body(signature);
        Assert.Contains("unexpected '{'", Assert.ThrowsExactly<ReplException>(() => onHeader.Apply("{")).Message);

        var onNextLine = new CellState(Resolver, GenericContext.Empty, [signature], signature, braceOpen: false);
        Assert.AreEqual(LineOutcome.Empty, onNextLine.Apply("{").Outcome);
        Assert.Contains("unexpected '{'", Assert.ThrowsExactly<ReplException>(() => onNextLine.Apply("{")).Message);
    }

    /// <summary>
    /// A method can call itself before it exists as a builder.
    /// </summary>
    [TestMethod]
    public void Apply_RecursiveCall_ResolvesOwnSignature()
    {
        var state = Body(Signature("Fib", typeof(int), (typeof(int), "n")), "ldc.i4 1", "call int32 Fib(int32)");
        Assert.AreEqual("[int32]", state.Stack.Render());
        var call = state.Entries[^1].Instruction!;
        Assert.IsTrue(((ResolvedMethod)call.Operand!).IsSessionMethod);
    }

    /// <summary>
    /// A boxed value is object to the model, which a reference return type accepts.
    /// </summary>
    [TestMethod]
    public void Apply_RetWithBoxedValueForInterface_IsAccepted()
    {
        var state = Body(Signature("Boxed", typeof(IComparable)), "ldc.i4 1", "box int32");
        Assert.AreEqual(LineOutcome.Instruction, state.Apply("ret").Outcome);

        var deref = Body(Signature("Deref", typeof(string), (typeof(string).MakeByRefType(), "s")), "ldarg s", "ldind.ref");
        Assert.AreEqual("[string]", deref.Stack.Render());
        Assert.AreEqual(LineOutcome.MethodEnd, deref.Apply("}").Outcome);
    }
}
