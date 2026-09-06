using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="Session"/>: cells compile on the real JIT and return real values.
/// </summary>
[TestClass]
public sealed class SessionTests
{
    private static Session Load(params string[] lines)
    {
        var session = new Session();
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        return session;
    }

    private static object? RunCell(params string[] lines) => Load(lines).Run().Value;

    /// <summary>
    /// A cell that leaves an int on the stack returns it boxed.
    /// </summary>
    [TestMethod]
    public void Run_Arithmetic_ReturnsValue()
    {
        Assert.AreEqual(42, RunCell("ldc.i4 6", "ldc.i4 7", "mul"));
        Assert.AreEqual(10L, RunCell("ldc.r8 2.5", "ldc.r8 4", "mul", "conv.i8"));
    }

    /// <summary>
    /// A cell that leaves nothing on the stack is void.
    /// </summary>
    [TestMethod]
    public void Run_EmptyStack_IsVoid()
    {
        var result = Load("ldc.i4 1", "pop").Run();
        Assert.IsTrue(result.IsVoid);
        Assert.IsNull(result.Value);
    }

    /// <summary>
    /// Console output from the cell is captured, not printed.
    /// </summary>
    [TestMethod]
    public void Run_ConsoleWrite_IsCaptured()
    {
        var result = Load("ldstr \"captured\"", "call void Console::WriteLine(string)").Run();
        Assert.AreEqual("captured" + Environment.NewLine, result.StandardOutput);
    }

    /// <summary>
    /// Locals and a backward branch make a loop.
    /// </summary>
    [TestMethod]
    public void Run_LoopWithLocals_Counts()
    {
        var value = RunCell(".locals init (int32 i)", "ldc.i4.0", "stloc i", "LOOP: ldloc i", "ldc.i4.1", "add", "dup", "stloc i", "ldc.i4 10", "blt LOOP", "ldloc i");
        Assert.AreEqual(10, value);
    }

    /// <summary>
    /// Locals survive a run; the cell body does not.
    /// </summary>
    [TestMethod]
    public void Run_ClearsBodyKeepsLocals()
    {
        var session = Load(".locals init (int32 i)", "ldc.i4 3", "stloc i", "ldloc i");
        Assert.AreEqual(3, session.Run().Value);
        Assert.IsTrue(session.State.IsEmpty);
        Assert.HasCount(1, session.State.Locals);
        Assert.AreEqual(1, session.CellsRun);
    }

    /// <summary>
    /// Inline ret is emitted when a forward label is pending, and a switch table selects a path.
    /// </summary>
    [TestMethod]
    public void Run_SwitchWithInlineRet_TakesSelectedPath()
    {
        var value = RunCell(".locals init (int32 x)", "ldc.i4 1", "stloc x", "ldloc x", "switch (A, B)", "ldstr \"default\"", "ret", "A: ldstr \"a\"", "ret", "B: ldstr \"b\"");
        Assert.AreEqual("b", value);
    }

    /// <summary>
    /// Exceptions thrown by the cell surface as CellException with the original inside.
    /// </summary>
    [TestMethod]
    public void Run_DivideByZero_ThrowsCellException()
    {
        var session = Load("ldc.i4 1", "ldc.i4 0", "div");
        var ex = Assert.ThrowsExactly<CellException>(() => session.Run());
        Assert.IsInstanceOfType<DivideByZeroException>(ex.InnerException);
    }

    /// <summary>
    /// try, catch, and finally blocks compile and run.
    /// </summary>
    [TestMethod]
    public void Run_TryCatchFinally_CatchesAndRunsFinally()
    {
        var session = Load(
            ".locals init (string m)",
            ".try {",
            "ldstr \"boom\"",
            "newobj instance void InvalidOperationException::.ctor(string)",
            "throw",
            "} catch InvalidOperationException {",
            "callvirt instance string Exception::get_Message()",
            "stloc m",
            "leave DONE",
            "} finally {",
            "ldstr \"finally\"",
            "call void Console::WriteLine(string)",
            "}",
            "DONE: ldloc m");
        var result = session.Run();
        Assert.AreEqual("boom", result.Value);
        Assert.Contains("finally", result.StandardOutput);
    }

    /// <summary>
    /// A filter decides whether its handler runs.
    /// </summary>
    [TestMethod]
    public void Run_FilterHandler_RunsWhenFilterMatches()
    {
        var value = RunCell(
            ".locals init (int32 n)",
            ".try {",
            "ldc.i4 1",
            "ldc.i4 0",
            "div",
            "stloc n",
            "leave END",
            "} filter {",
            "isinst DivideByZeroException",
            "ldnull",
            "cgt.un",
            "endfilter",
            "} handler {",
            "pop",
            "ldc.i4 42",
            "stloc n",
            "leave END",
            "}",
            "END: ldloc n");
        Assert.AreEqual(42, value);
    }

    /// <summary>
    /// A fault handler runs on the exception path and the exception continues.
    /// </summary>
    [TestMethod]
    public void Run_FaultHandler_RunsThenRethrows()
    {
        var session = Load(
            ".try {",
            "ldnull",
            "throw",
            "} fault {",
            "ldstr \"fault ran\"",
            "call void Console::WriteLine(string)",
            "}");
        var ex = Assert.ThrowsExactly<CellException>(() => session.Run());
        Assert.IsInstanceOfType<NullReferenceException>(ex.InnerException);
    }

    /// <summary>
    /// Block structure is validated as lines arrive.
    /// </summary>
    [TestMethod]
    public void AddLine_BlockMistakes_AreReported()
    {
        Assert.Contains("no protected region", Assert.ThrowsExactly<ReplException>(() => Load("}")).Message);
        Assert.Contains("needs a handler", Assert.ThrowsExactly<ReplException>(() => Load(".try {", "nop", "}")).Message);
        Assert.Contains("endfilter", Assert.ThrowsExactly<ReplException>(() => Load(".try {", "nop", "} filter {", "pop", "} handler {")).Message);
        Assert.Contains("leave", Assert.ThrowsExactly<ReplException>(() => Load(".try {", "ldc.i4 1", "ret")).Message);
        Assert.Contains("still open", Assert.ThrowsExactly<ReplException>(() => Load(".try {", "nop", "} finally {", "nop").Run()).Message);
    }

    /// <summary>
    /// calli through a function pointer from ldftn.
    /// </summary>
    [TestMethod]
    public void Run_Calli_CallsThroughPointer()
    {
        Assert.AreEqual(9, RunCell("ldc.i4 3", "ldc.i4 9", "ldftn int32 Math::Max(int32, int32)", "calli int32(int32, int32)"));
    }

    /// <summary>
    /// Arguments declared with .args are passed on every run.
    /// </summary>
    [TestMethod]
    public void Run_Arguments_ArePassed()
    {
        var session = Load(".args (int32 x = 5, string s = \"ab\")", "ldarg x", "ldarg.0", "mul");
        Assert.AreEqual(25, session.Run().Value);
        session.AddLine("ldarg s");
        session.AddLine("callvirt instance int32 String::get_Length()");
        Assert.AreEqual(2, session.Run().Value);
    }

    /// <summary>
    /// A generic cell binds its parameters with .typeargs.
    /// </summary>
    [TestMethod]
    public void Run_GenericCell_BindsTypeArguments()
    {
        var session = Load(".typeparams (T)", ".typeargs (int32)", "ldc.i4 7", "box int32", "unbox.any !!T", "box !!T");
        Assert.AreEqual(7, session.Run().Value);
    }

    /// <summary>
    /// A generic cell without bound type arguments is refused with guidance.
    /// </summary>
    [TestMethod]
    public void Run_GenericCellWithoutTypeArgs_Explains()
    {
        var session = Load(".typeparams (T)", ".locals init (!!T v)", "ldloc v", "box !!T");
        var ex = Assert.ThrowsExactly<ReplException>(() => session.Run());
        Assert.Contains(".typeargs", ex.Message);
    }

    /// <summary>
    /// Members on types instantiated over a cell type parameter resolve through the definition.
    /// </summary>
    [TestMethod]
    public void Run_GenericInstantiationOverCellParameter_Resolves()
    {
        var session = Load(
            ".typeparams (T)",
            ".typeargs (string)",
            "newobj instance void class [System.Collections]System.Collections.Generic.List`1<!!T>::.ctor()",
            "callvirt instance int32 class [System.Collections]System.Collections.Generic.List`1<!!T>::get_Count()");
        Assert.AreEqual(0, session.Run().Value);
    }

    /// <summary>
    /// Pointers from localloc and byrefs from ldloca work.
    /// </summary>
    [TestMethod]
    public void Run_LocallocAndByref_Work()
    {
        Assert.AreEqual(9, RunCell("ldc.i4 16", "localloc", "dup", "ldc.i4 9", "stind.i4", "ldind.i4"));
        Assert.AreEqual(8, RunCell(".locals init (int32 v, int32& r)", "ldc.i4 7", "stloc v", "ldloca v", "stloc r", "ldloc r", "ldc.i4 8", "stind.i4", "ldloc v"));
    }

    /// <summary>
    /// Generic methods instantiate from the reference and use !!0 in parameters.
    /// </summary>
    [TestMethod]
    public void Run_GenericMethodInstantiation_Works()
    {
        var value = RunCell(
            "ldc.i4 2", "newarr string", "dup", "ldc.i4 0", "ldstr \"x\"", "stelem.ref",
            "call !!0 [System.Linq]System.Linq.Enumerable::First<string>(class [System.Runtime]System.Collections.Generic.IEnumerable`1<!!0>)");
        Assert.AreEqual("x", value);
    }

    /// <summary>
    /// Undo removes the last body line and Reset drops everything.
    /// </summary>
    [TestMethod]
    public void UndoAndReset_RestoreState()
    {
        var session = Load(".locals init (int32 i)", "ldc.i4 1", "ldc.i4 2");
        Assert.IsTrue(session.Undo());
        Assert.AreEqual("[int32]", session.State.Stack.Render());
        session.Reset();
        Assert.IsEmpty(session.State.Locals);
        Assert.IsFalse(session.Undo());
    }

    /// <summary>
    /// Vararg cells are refused by the runtime outside Windows, and the message says so.
    /// </summary>
    [TestMethod]
    public void Run_VarargCell_ExplainsPlatformSupport()
    {
        var session = Load(".vararg", "arglist", "pop", "ldc.i4 1");
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(1, session.Run().Value);
            return;
        }

        var ex = Assert.ThrowsExactly<ReplException>(() => session.Run());
        Assert.Contains("Windows", ex.Message);
    }

    /// <summary>
    /// arglist needs the vararg directive.
    /// </summary>
    [TestMethod]
    public void AddLine_ArglistWithoutVararg_Throws()
    {
        var ex = Assert.ThrowsExactly<ReplException>(() => Load("arglist"));
        Assert.Contains(".vararg", ex.Message);
    }

    /// <summary>
    /// The runtime's rejection of a stack mismatch between branches is reported as a REPL error.
    /// </summary>
    [TestMethod]
    public void Run_BranchStackMismatch_ReportsJitRejection()
    {
        var session = Load("ldc.i4 0", "brfalse SKIP", "ldc.i4 1", "ldc.i4 2", "pop", "SKIP: pop");
        var ex = Assert.ThrowsExactly<ReplException>(() => session.Run());
        Assert.Contains("JIT rejected", ex.Message);
    }
}
