using IlRepl.Engine;
using IlRepl.Protocol;

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
    public void Run_BranchStackMismatch_IsRefusedBeforeExecution()
    {
        var session = Load("ldc.i4 0", "brfalse SKIP", "ldc.i4 1", "ldc.i4 2", "pop");
        var ex = Assert.ThrowsExactly<ReplException>(() => session.AddLine("SKIP: pop"));
        Assert.Contains("SKIP receives incompatible stacks", ex.Message);
        Assert.AreEqual(0, session.CellsRun);
    }

    /// <summary>
    /// A block comment opened on one line and closed on a later one is one comment, inside a
    /// method as anywhere else; the text after the closing delimiter is parsed.
    /// </summary>
    [TestMethod]
    public void Normalize_CommentSpansLinesInsideMethod()
    {
        var session = Load(".method int32 F() {", "/* open", "still */ ldc.i4.1", "ret", "}", "call int32 F()");
        Assert.AreEqual(1, session.Run().Value);
    }

    /// <summary>
    /// A closing brace inside a comment closes nothing.
    /// </summary>
    [TestMethod]
    public void Normalize_CommentCoversBrace_DoesNotClose()
    {
        var session = Load(".method int32 F() {", "ldc.i4 1", "/* } */", "/*", "}", "*/");
        Assert.AreEqual("F", session.OpenMethod?.Name);
        session.AddLine("ret");
        session.AddLine("}");
        Assert.IsNull(session.OpenMethod);
        Assert.HasCount(1, session.Methods);
    }

    /// <summary>
    /// The state carries through nested class and method blocks.
    /// </summary>
    [TestMethod]
    public void Normalize_NestedTypeAndMethod_KeepsState()
    {
        var session = Load(".class public C {", "/* about C", "*/ .method public static int32 One() {", "ldc.i4 1 /* one", "*/ ret", "}", "}", "call int32 C::One()");
        Assert.AreEqual(1, session.Run().Value);
    }

    /// <summary>
    /// Whitespace outside a comment is blank; a comment alone, or a blank line inside an open
    /// comment, is a comment; anything left is text.
    /// </summary>
    [TestMethod]
    public void Normalize_Kinds()
    {
        var session = new Session();
        Assert.AreEqual(SourceLineKind.Blank, session.Normalize("   ").Kind);
        Assert.AreEqual(SourceLineKind.Comment, session.Normalize("// note").Kind);
        Assert.AreEqual(SourceLineKind.Comment, session.Normalize("/* a */").Kind);
        var text = session.Normalize("nop // note");
        Assert.AreEqual(SourceLineKind.Text, text.Kind);
        Assert.AreEqual("nop", text.Text);
        Assert.AreEqual("nop // note", text.Raw);
        Assert.IsFalse(text.InBlockCommentBefore);

        Assert.AreEqual(SourceLineKind.Comment, session.Normalize("/* open").Kind);
        Assert.IsTrue(session.InBlockComment);
        var inside = session.Normalize("   ");
        Assert.AreEqual(SourceLineKind.Comment, inside.Kind);
        Assert.IsTrue(inside.InBlockCommentBefore);
        var closing = session.Normalize("still */ nop");
        Assert.AreEqual(SourceLineKind.Text, closing.Kind);
        Assert.AreEqual("nop", closing.Text);
        Assert.IsTrue(closing.InBlockCommentBefore);
        Assert.IsFalse(session.InBlockComment);
    }

    /// <summary>
    /// Stored lines hold no comments, so a replay after an undo needs no comment state.
    /// </summary>
    [TestMethod]
    public void Undo_AfterCommentedLines_ReplaysCleanText()
    {
        var session = Load(".method int32 F() {", "/* c */ ldc.i4 1", "ldc.i4 2 // two");
        Assert.IsTrue(session.Undo());
        Assert.AreEqual(1, session.State.Stack.Count);
        session.AddLine("ret");
        session.AddLine("}");
        Assert.AreEqual("ldc.i4 1", session.Methods[0].BodyLines[0]);
        session.AddLine("call int32 F()");
        Assert.AreEqual(1, session.Run().Value);
    }

    /// <summary>
    /// A line the session refuses leaves the comment state where it was, through the string
    /// overload as well.
    /// </summary>
    [TestMethod]
    public void AddLine_RefusedString_LeavesNoCommentOpen()
    {
        var session = new Session();
        Assert.ThrowsExactly<ReplException>(() => session.AddLine("bogus /*"));
        Assert.IsFalse(session.InBlockComment);
        Assert.AreEqual(LineOutcome.Instruction, session.AddLine("nop").Outcome);
    }
}
