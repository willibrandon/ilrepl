using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="Session"/> with <c>.method</c> blocks: definition, persistence,
/// redefinition, undo, and running cells that call the methods on the real JIT.
/// </summary>
[TestClass]
public sealed class SessionMethodTests
{
    private static readonly string[] Fib =
    [
        ".method int32 Fib(int32 n) {",
        "ldarg n",
        "ldc.i4 2",
        "blt BASE",
        "ldarg n",
        "ldc.i4 1",
        "sub",
        "call int32 Fib(int32)",
        "ldarg n",
        "ldc.i4 2",
        "sub",
        "call int32 Fib(int32)",
        "add",
        "ret",
        "BASE: ldarg n",
        "ret",
        "}",
    ];

    private static readonly string[] Two = [".method int32 Two() {", "ldc.i4 2", "ret", "}"];

    private static Session Load(params string[] lines)
    {
        var session = new Session();
        Add(session, lines);
        return session;
    }

    private static void Add(Session session, params string[] lines)
    {
        foreach (var line in lines)
        {
            session.AddLine(line);
        }
    }

    private static object? RunCell(Session session, params string[] lines)
    {
        Add(session, lines);
        return session.Run().Value;
    }

    /// <summary>
    /// A header opens a method state whose parameters are in scope.
    /// </summary>
    [TestMethod]
    public void AddLine_MethodHeader_OpensMethodState()
    {
        var session = new Session();
        var result = session.AddLine(".method int32 Fib(int32 n) {");
        Assert.AreEqual(LineOutcome.MethodStart, result.Outcome);
        Assert.AreEqual("method int32 Fib(int32 n)", result.Message);
        Assert.AreEqual("Fib", session.OpenMethod!.Name);
        Assert.IsTrue(session.State.IsMethod);
        Assert.HasCount(1, session.State.Arguments);
        Assert.IsEmpty(session.Methods);
    }

    /// <summary>
    /// The brace may follow the header on its own line.
    /// </summary>
    [TestMethod]
    public void AddLine_BraceOnNextLine_IsAccepted()
    {
        var session = Load(".method int32 One()", "{", "ldc.i4 1", "}");
        Assert.HasCount(1, session.Methods);
        Assert.IsNull(session.OpenMethod);
        Assert.AreEqual(1, RunCell(session, "call int32 One()"));
    }

    /// <summary>
    /// The closing brace commits the method with its body lines.
    /// </summary>
    [TestMethod]
    public void AddLine_MethodClose_RegistersMethod()
    {
        var session = new Session();
        LineResult last = new(LineOutcome.Empty, null, null);
        foreach (var line in Fib)
        {
            last = session.AddLine(line);
        }

        Assert.AreEqual(LineOutcome.MethodEnd, last.Outcome);
        Assert.AreEqual("end of method Fib", last.Message);
        Assert.IsNull(session.OpenMethod);
        Assert.HasCount(1, session.Methods);
        Assert.AreEqual("int32 Fib(int32 n)", session.Methods[0].Signature.DescribeWithNames());
        Assert.HasCount(Fib.Length - 2, session.Methods[0].BodyLines);
        Assert.AreEqual(".method int32 Fib(int32 n) {", session.Methods[0].HeaderLine);
    }

    /// <summary>
    /// Locals declared inside a method belong to it, not to the cell.
    /// </summary>
    [TestMethod]
    public void AddLine_LocalsInsideMethod_StayInMethod()
    {
        var session = Load(".method int32 Three() {", ".locals init (int32 t)", "ldc.i4 3", "stloc t", "ldloc t", "ret", "}");
        Assert.IsEmpty(session.Cell.Locals);
        Assert.IsEmpty(session.DeclarationLines);
        Assert.HasCount(1, session.Methods[0].State.Locals);
        Assert.AreEqual(3, RunCell(session, "call int32 Three()"));
    }

    /// <summary>
    /// Cell-level directives are refused inside a method.
    /// </summary>
    [TestMethod]
    public void AddLine_TypeargsInsideMethod_Throws()
    {
        var session = Load(".method void F() {");
        Assert.Contains("close the method with } first", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".typeargs (int32)")).Message);
        Assert.Contains("not allowed inside a method", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".typeparams (T)")).Message);
        Assert.Contains("already open (F)", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method void G() {")).Message);
    }

    /// <summary>
    /// A call to a name the session does not have lists what it does have.
    /// </summary>
    [TestMethod]
    public void AddLine_UnknownSessionMethod_ListsDefined()
    {
        var session = Load(Fib);
        Assert.Contains("no method 'Fibb' in the session; defined: int32 Fib(int32)  (define one with .method)", Assert.ThrowsExactly<ReplException>(() => session.AddLine("call int32 Fibb(int32)")).Message);
    }

    /// <summary>
    /// Run and Invoke are the cell's.
    /// </summary>
    [TestMethod]
    public void AddLine_ReservedMethodName_Throws()
    {
        var session = new Session();
        Assert.Contains("reserved", Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method object Run() {")).Message);
        Assert.IsNull(session.OpenMethod);
        Assert.AreEqual(0, session.Submissions);
    }

    /// <summary>
    /// Defining a name again replaces the method when the block closes, in place.
    /// </summary>
    [TestMethod]
    public void AddLine_RedefinitionHeader_ReplacesOnClose()
    {
        var session = Load([.. Two, ".method int32 Three() {", "ldc.i4 3", "ret", "}"]);
        Add(session, ".method int32 Two() {", "ldc.i4 20");
        Assert.HasCount(2, session.Methods, "the old definition stays until the close");
        var close = session.AddLine("}");
        Assert.AreEqual("replaced method Two", close.Message);
        Assert.HasCount(2, session.Methods);
        Assert.AreEqual("Two", session.Methods[0].Signature.Name, "a replacement keeps its position");
        Assert.AreEqual(20, RunCell(session, "call int32 Two()"));
    }

    /// <summary>
    /// A new signature that breaks another method is refused at the header, and the old method stays.
    /// </summary>
    [TestMethod]
    public void AddLine_RedefinitionBreakingOtherMethod_FailsAtHeaderAndKeepsOld()
    {
        var session = Load([.. Two, ".method int32 Twice() {", "call int32 Two()", "ldc.i4 2", "mul", "ret", "}"]);
        var ex = Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method int64 Two() {"));
        Assert.Contains("cannot redefine Two as int64 Two(): method Twice would no longer compile", ex.Message);
        Assert.Contains("(the previous definition stays)", ex.Message);
        Assert.IsNull(session.OpenMethod);
        Assert.AreEqual(typeof(int), session.Methods[0].Signature.ReturnType);
        Assert.AreEqual(2, session.Submissions);
        Assert.AreEqual(4, RunCell(session, "call int32 Twice()"));
    }

    /// <summary>
    /// A new signature that breaks the cell body is refused at the header with a way out.
    /// </summary>
    [TestMethod]
    public void AddLine_RedefinitionBreakingCellBody_FailsAtHeader()
    {
        var session = Load([.. Two, "call int32 Two()"]);
        var ex = Assert.ThrowsExactly<ReplException>(() => session.AddLine(".method int64 Two() {"));
        Assert.Contains("the cell body would no longer compile", ex.Message);
        Assert.Contains("(.clear the cell first, or keep the signature)", ex.Message);
        Assert.AreEqual("[int32]", session.State.Stack.Render(), "the cell is untouched");

        session.ClearCell();
        Assert.AreEqual(LineOutcome.MethodStart, session.AddLine(".method int64 Two() {").Outcome);
    }

    /// <summary>
    /// Undo inside a block takes back the block's last line.
    /// </summary>
    [TestMethod]
    public void Undo_InsideMethod_PopsMethodLine()
    {
        var session = Load("ldc.i4 7", ".method int32 F() {", "ldc.i4 1", "ldc.i4 2");
        Assert.IsTrue(session.Undo());
        Assert.AreEqual("F", session.OpenMethod!.Name);
        Assert.AreEqual("[int32]", session.State.Stack.Render());
        Assert.AreEqual("[int32]", session.Cell.Stack.Render(), "the cell keeps its line");
        Assert.AreEqual(LineOutcome.MethodEnd, session.AddLine("}").Outcome);
    }

    /// <summary>
    /// Undo on the header abandons the block without counting a submission.
    /// </summary>
    [TestMethod]
    public void Undo_OnMethodHeader_AbandonsMethod()
    {
        var session = Load(".method int32 F() {");
        Assert.IsTrue(session.Undo());
        Assert.IsNull(session.OpenMethod);
        Assert.IsEmpty(session.Methods);
        Assert.AreEqual(0, session.Submissions);
        Assert.IsFalse(session.Undo(), "nothing is left to undo");
    }

    /// <summary>
    /// Abandoning a block leaves the cell and the committed methods alone.
    /// </summary>
    [TestMethod]
    public void AbandonMethod_WhileOpen_KeepsCellAndTable()
    {
        var session = Load([.. Two, "ldc.i4 7", ".method int32 F() {", "ldc.i4 1"]);
        Assert.IsTrue(session.AbandonMethod());
        Assert.IsNull(session.OpenMethod);
        Assert.AreEqual("[int32]", session.State.Stack.Render());
        Assert.HasCount(1, session.Methods);
        Assert.AreEqual(1, session.Submissions);
        Assert.IsFalse(session.AbandonMethod());
    }

    /// <summary>
    /// Clearing the cell keeps the methods, like the declarations.
    /// </summary>
    [TestMethod]
    public void ClearCell_KeepsMethods()
    {
        var session = Load([.. Two, "ldc.i4 1"]);
        session.ClearCell();
        Assert.HasCount(1, session.Methods);
        Assert.IsEmpty(session.BodyLines);
        Assert.AreEqual(2, RunCell(session, "call int32 Two()"));
    }

    /// <summary>
    /// Reset drops the methods and any open block.
    /// </summary>
    [TestMethod]
    public void Reset_DropsMethods()
    {
        var session = Load([.. Two, ".method int32 F() {"]);
        session.Reset();
        Assert.IsEmpty(session.Methods);
        Assert.IsNull(session.OpenMethod);
        Assert.Contains("no method 'Two' in the session (define one with .method", Assert.ThrowsExactly<ReplException>(() => session.AddLine("call int32 Two()")).Message);
    }

    /// <summary>
    /// The cell cannot run while a block is open.
    /// </summary>
    [TestMethod]
    public void Run_WhileMethodOpen_Throws()
    {
        var session = Load("ldc.i4 1", ".method int32 Fib(int32 n) {");
        Assert.Contains("method Fib is still open; close it with }", Assert.ThrowsExactly<ReplException>(() => session.Run()).Message);
        Assert.AreEqual("Fib", session.OpenMethod!.Name);
        Assert.AreEqual(0, session.CellsRun);
    }

    /// <summary>
    /// The cell cannot be saved while a block is open.
    /// </summary>
    [TestMethod]
    public void Save_WhileMethodOpen_Throws()
    {
        var session = Load("ldc.i4 1", ".method int32 Fib(int32 n) {");
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-tests", Guid.NewGuid().ToString("N") + ".dll");
        Assert.Contains("method Fib is still open", Assert.ThrowsExactly<ReplException>(() => session.Save(path)).Message);
        Assert.IsFalse(File.Exists(path));
    }

    /// <summary>
    /// While a block is open, State is the method and Cell is still the cell.
    /// </summary>
    [TestMethod]
    public void State_WhileMethodOpen_IsMethodState()
    {
        var session = Load("ldc.i4 7", ".method int32 F() {");
        Assert.IsTrue(session.State.IsMethod);
        Assert.AreEqual("[]", session.State.Stack.Render());
        Assert.IsFalse(session.Cell.IsMethod);
        Assert.AreEqual("[int32]", session.Cell.Stack.Render());
    }

    /// <summary>
    /// A committed block counts one submission; the braces inside it and the run count do not move.
    /// </summary>
    [TestMethod]
    public void Submissions_AdvanceOnceOnCommit()
    {
        var session = Load(".method void Guarded() {", ".try {", "nop", "} finally {", "nop", "}");
        Assert.AreEqual(0, session.Submissions, "closing the region is not a submission");
        session.AddLine("ret");
        Assert.AreEqual(0, session.Submissions, "ret inside the block is not a submission");
        session.AddLine("}");
        Assert.AreEqual(1, session.Submissions);
        Assert.AreEqual(0, session.CellsRun);
        RunCell(session, "call void Guarded()");
        Assert.AreEqual(2, session.Submissions);
        Assert.AreEqual(1, session.CellsRun);
    }

    /// <summary>
    /// A refused close and an abandoned block leave the count alone.
    /// </summary>
    [TestMethod]
    public void Submissions_UnchangedOnRejectedOrAbandonedClose()
    {
        var session = Load(".method int32 F() {", "ldc.i4 1", "ldc.i4 2");
        Assert.ThrowsExactly<ReplException>(() => session.AddLine("}"));
        Assert.AreEqual(0, session.Submissions);
        Assert.AreEqual("F", session.OpenMethod!.Name);
        session.AbandonMethod();
        Assert.AreEqual(0, session.Submissions);
    }

    /// <summary>
    /// Closing a block neither runs nor discards what the cell already holds.
    /// </summary>
    [TestMethod]
    public void AddLine_MethodClose_KeepsPendingCellInstructions()
    {
        var session = Load(["ldc.i4 1", .. Two]);
        Assert.AreEqual(1, session.Cell.InstructionCount);
        Assert.AreEqual(1, session.Submissions);
        Assert.AreEqual(0, session.CellsRun);
        Assert.AreEqual(1, session.Run().Value);
        Assert.AreEqual(2, session.Submissions);
    }

    /// <summary>
    /// The transcript from issue #1.
    /// </summary>
    [TestMethod]
    public void Run_FibFromIssue_Returns55()
    {
        var session = Load(Fib);
        Assert.AreEqual(55, RunCell(session, "ldc.i4 10", "call int32 Fib(int32)"));
    }

    /// <summary>
    /// A method outlives the cell that defined it.
    /// </summary>
    [TestMethod]
    public void Run_MethodPersistsAcrossCells()
    {
        var session = Load(Two);
        Assert.AreEqual(2, RunCell(session, "call int32 Two()"));
        Assert.AreEqual(5, RunCell(session, "call int32 Two()", "ldc.i4 3", "add"));
        Assert.HasCount(1, session.Methods);
    }

    /// <summary>
    /// A void method with no ret of its own gets one at the brace.
    /// </summary>
    [TestMethod]
    public void Run_VoidMethodWithImplicitRet_Works()
    {
        var session = Load(".method void Hi() {", "ldstr \"hi\"", "call void Console::WriteLine(string)", "}");
        Add(session, "call void Hi()");
        var result = session.Run();
        Assert.IsTrue(result.IsVoid);
        Assert.Contains("hi", result.StandardOutput);
    }

    /// <summary>
    /// Locals, protected regions, and labels work inside a method.
    /// </summary>
    [TestMethod]
    public void Run_MethodWithLocalsAndTry_Works()
    {
        var session = Load(
            ".method int32 Safe() {",
            ".locals init (int32 r)",
            ".try {",
            "ldc.i4 1",
            "ldc.i4 0",
            "div",
            "stloc r",
            "leave END",
            "} catch DivideByZeroException {",
            "pop",
            "ldc.i4 42",
            "stloc r",
            "leave END",
            "}",
            "END: ldloc r",
            "ret",
            "}");
        Assert.AreEqual(42, RunCell(session, "call int32 Safe()"));
    }

    /// <summary>
    /// A method can call another one defined earlier.
    /// </summary>
    [TestMethod]
    public void Run_TwoMethodsCallEachOther_Works()
    {
        var session = Load([.. Two, ".method int32 Twice() {", "call int32 Two()", "ldc.i4 2", "mul", "ret", "}"]);
        Assert.AreEqual(4, RunCell(session, "call int32 Twice()"));
    }

    /// <summary>
    /// Mutual recursion: a placeholder, the caller, then the real body under the same signature.
    /// </summary>
    [TestMethod]
    public void Run_MutualRecursionViaRedefinition_Works()
    {
        var session = Load(
            ".method bool IsEven(int32 n) {", "ldc.i4 1", "ret", "}",
            ".method bool IsOdd(int32 n) {", "ldarg n", "brfalse ZERO", "ldarg n", "ldc.i4 1", "sub", "call bool IsEven(int32)", "ret", "ZERO: ldc.i4 0", "ret", "}",
            ".method bool IsEven(int32 n) {", "ldarg n", "brfalse ZERO", "ldarg n", "ldc.i4 1", "sub", "call bool IsOdd(int32)", "ret", "ZERO: ldc.i4 1", "ret", "}");
        Assert.IsTrue((bool)RunCell(session, "ldc.i4 7", "call bool IsOdd(int32)")!);
        Assert.IsTrue((bool)RunCell(session, "ldc.i4 10", "call bool IsEven(int32)")!);
        Assert.IsFalse((bool)RunCell(session, "ldc.i4 10", "call bool IsOdd(int32)")!);
    }

    /// <summary>
    /// The same label name can appear in a method and in the cell.
    /// </summary>
    [TestMethod]
    public void Run_LabelsAreScopedPerMethod()
    {
        var session = Load(".method int32 One() {", "ldc.i4 1", "br L", "L: ret", "}");
        Assert.AreEqual(1, RunCell(session, "br L", "L: call int32 One()"));
    }

    /// <summary>
    /// ldftn takes a session method and calli calls through it.
    /// </summary>
    [TestMethod]
    public void Run_LdftnAndCalliOnSessionMethod_Works()
    {
        var session = Load(Fib);
        Assert.AreEqual(6765, RunCell(session, "ldc.i4 20", "ldftn int32 Fib(int32)", "calli int32(int32)"));
    }

    /// <summary>
    /// A delegate can be built over a session method.
    /// </summary>
    [TestMethod]
    public void Run_DelegateOverSessionMethod_Works()
    {
        var session = Load(Fib);
        Assert.AreEqual(610, RunCell(session,
            "ldnull",
            "ldftn int32 Fib(int32)",
            "newobj instance void class Func`2<int32, int32>::.ctor(object, native int)",
            "ldc.i4 15",
            "callvirt instance !1 class Func`2<int32, int32>::Invoke(!0)"));
    }

    /// <summary>
    /// ldtoken of a session method yields its handle.
    /// </summary>
    [TestMethod]
    public void Run_LdtokenSessionMethod_ResolvesToMethodName()
    {
        var session = Load(Fib);
        Assert.AreEqual("Fib", RunCell(session,
            "ldtoken method int32 Fib(int32)",
            "call class [System.Runtime]System.Reflection.MethodBase [System.Runtime]System.Reflection.MethodBase::GetMethodFromHandle(valuetype [System.Runtime]System.RuntimeMethodHandle)",
            "callvirt instance string [System.Runtime]System.Reflection.MemberInfo::get_Name()"));
    }

    /// <summary>
    /// Reference return types and string parameters work.
    /// </summary>
    [TestMethod]
    public void Run_MethodReturningReferenceType_Works()
    {
        var session = Load(".method string Greet(string name) {", "ldstr \"hi \"", "ldarg name", "call string String::Concat(string, string)", "ret", "}");
        Assert.AreEqual("hi x", RunCell(session, "ldstr \"x\"", "call string Greet(string)"));
        Assert.AreEqual("hi x", RunCell(session, "ldstr \"x\"", "call Greet(string)"), "the return type is optional in the call");
    }

    /// <summary>
    /// After a replacement the next run uses the new body.
    /// </summary>
    [TestMethod]
    public void Run_RedefinedMethod_UsesNewBody()
    {
        var session = Load(Two);
        Assert.AreEqual(2, RunCell(session, "call int32 Two()"));
        Add(session, ".method int32 Two() {", "ldc.i4 3", "ret", "}");
        Assert.AreEqual(3, RunCell(session, "call int32 Two()"));
    }
}
