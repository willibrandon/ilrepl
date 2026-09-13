using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Exercises source control flow through accepted cells and named methods.
/// </summary>
[TestClass]
public sealed class ControlFlowSessionTests
{
    /// <summary>
    /// Alternative values meet at one stack slot instead of accumulating in source order.
    /// </summary>
    [TestMethod]
    public void Diamond_DefinesAndRunsBothPaths()
    {
        var session = new Session();
        Add(session, ".method int32 Choose(int32 flag) {", "ldarg flag", "brtrue TRUE", "ldc.i4.1", "br DONE",
            "TRUE: ldc.i4.2", "DONE: ret", "}");
        Add(session, "ldc.i4.0", "call int32 Choose(int32)");
        Assert.AreEqual(1, session.Run().Value);
        Add(session, "ldc.i4.1", "call int32 Choose(int32)");
        Assert.AreEqual(2, session.Run().Value);
    }

    /// <summary>
    /// Dead instructions have no invented input stack and do not trigger underflow.
    /// </summary>
    [TestMethod]
    public void UnreachablePop_IsRetained()
    {
        var session = new Session();
        Add(session, ".method void Dead() {", "br DONE", "pop");
        Assert.AreEqual("unreachable", session.State.StackText);
        Add(session, "DONE: ret", "}", "call void Dead()");
        Assert.IsTrue(session.Run().IsVoid);
    }

    /// <summary>
    /// An incrementally appended unreachable instruction still has its operand shape checked.
    /// </summary>
    [TestMethod]
    public void UnreachableInvalidOperand_IsRefusedDuringIncrementalAppend()
    {
        var session = new Session();
        Add(session, ".method int32 Dead() {", "ldnull", "throw", "ldc.i4.1");
        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine("newarr int32&"));
        Assert.Contains("array element type", error.Message);
        Assert.AreEqual("unreachable", session.State.StackText);
        Add(session, "newarr int32", "pop", "ldnull", "throw", "}");
    }

    /// <summary>
    /// An incrementally appended unreachable instruction retains the full analysis diagnostics.
    /// </summary>
    [TestMethod]
    public void UnreachableAppend_MatchesFullAnalysisDiagnostics()
    {
        var session = new Session();
        Add(session, ".method void Dead() {", ".locals init (int32* pointer)", "ldnull", "throw", "ldloc pointer");
        var incremental = session.State.Diagnostics;
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable, incremental);
        Add(session, "FULL:");
        Assert.AreSequenceEqual(incremental, session.State.Diagnostics);
    }

    /// <summary>
    /// A depth conflict refuses its defining line and identifies both incoming paths.
    /// </summary>
    [TestMethod]
    public void JoinMismatch_ReportsPathsBeforeJit()
    {
        var session = new Session();
        Add(session, ".method void Bad(int32 flag) {", "ldarg flag", "brfalse DONE", "ldc.i4.1");
        var error = Assert.Throws<ReplException>(() => session.AddLine("DONE: pop"));
        Assert.Contains("DONE receives incompatible stacks", error.Message);
        Assert.Contains("[]", error.Message);
        Assert.Contains("[int32]", error.Message);
        Assert.DoesNotContain("JIT", error.Message);
        Assert.DoesNotContain("DONE", session.State.DefinedLabels);
        Add(session, "pop", "DONE: ret", "}");
    }

    /// <summary>
    /// Same-height incompatible types are rejected instead of merging into an unknown slot.
    /// </summary>
    [TestMethod]
    public void JoinTypeMismatch_IsNotUnknown()
    {
        var session = new Session();
        Add(session, "ldc.i4.0", "brtrue TEXT", "ldc.i4.1", "br DONE", "TEXT: ldstr \"s\"");
        var error = Assert.Throws<ReplException>(() => session.AddLine("DONE: pop"));
        Assert.Contains("[int32]", error.Message);
        Assert.Contains("[string]", error.Message);
    }

    /// <summary>
    /// A later edge rechecks a previously unreachable instruction and can reveal underflow.
    /// </summary>
    [TestMethod]
    public void BackEdge_RechecksEarlierInstructions()
    {
        var session = new Session();
        Add(session, "br NEXT", "EARLIER: pop", "NEXT: nop");
        var error = Assert.Throws<ReplException>(() => session.AddLine("br EARLIER"));
        Assert.Contains("stack underflow", error.Message);
    }

    /// <summary>
    /// An unresolved forward label remains an incomplete finding while the body is editable.
    /// </summary>
    [TestMethod]
    public void ForwardTarget_IsIncompleteUntilDefined()
    {
        var session = new Session();
        Add(session, "br END");
        Assert.Contains(d => d.Kind == AnalysisDiagnosticKind.Incomplete, session.State.Diagnostics);
        Add(session, "END: ldc.i4 42");
        Assert.DoesNotContain(d => d.Kind == AnalysisDiagnosticKind.Incomplete, session.State.Diagnostics);
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Appended straight-line instructions retain the same stack when a later label rebuilds the full graph.
    /// </summary>
    [TestMethod]
    public void StraightLineAppend_FullRebuildRetainsTheStack()
    {
        var session = new Session();
        Add(session, ".locals init (int32 value)", "ldc.i4.s 40", "stloc value", "ldloc value", "conv.i8",
            "ldc.i8 2", "add", "conv.i4");
        var incremental = session.State.StackText;
        Add(session, "VALUE:");
        Assert.AreEqual(incremental, session.State.StackText);
        Add(session, "ret");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// Leave cannot enter a try region even when it targets the region's first instruction.
    /// </summary>
    [TestMethod]
    public void LeaveIntoTry_IsRejected()
    {
        var session = new Session();
        Add(session, "leave INSIDE", ".try {");
        var error = Assert.Throws<ReplException>(() => session.AddLine("INSIDE: leave DONE"));
        Assert.Contains("leave cannot transfer control", error.Message);
    }

    /// <summary>
    /// A protected-region boundary cannot appear between a prefix and its instruction.
    /// </summary>
    /// <param name="handler">Whether the boundary begins a handler instead of a try.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrefixBeforeProtectedRegionBoundary_IsRejected(bool handler)
    {
        var session = new Session();
        if (handler)
        {
            Add(session, ".try {", "leave DONE", "volatile.");
        }
        else
        {
            Add(session, "volatile.");
        }

        var boundary = handler ? "} catch [System.Runtime]System.Exception {" : ".try {";
        var error = Assert.Throws<ReplException>(() => session.AddLine(boundary));
        Assert.Contains("protected-region boundary", error.Message);
    }

    /// <summary>
    /// The return synthesized at a method close completes a tail call exactly as an explicit ret does.
    /// </summary>
    [TestMethod]
    public void TailCall_ImplicitReturnClosesAndRuns()
    {
        var session = new Session();
        Add(session, ".method int32 Tail(int32 value) {", "ldarg value", "tail.",
            "call int32 [System.Runtime]System.Math::Abs(int32)", "}");
        Add(session, "ldc.i4.s -42", "call int32 Tail(int32)");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// A cell may use its synthesized return directly after a tail call that already returns a reference.
    /// </summary>
    [TestMethod]
    public void TailCall_CellReferenceReturnRuns()
    {
        var session = new Session();
        Add(session, "ldstr \"forty\"", "ldstr \"two\"", "tail.",
            "call string string::Concat(string, string)");
        Assert.AreEqual("fortytwo", session.Run().Value);
    }

    /// <summary>
    /// A cell refuses a trailing value-type call because boxing would separate the tail call and return.
    /// </summary>
    [TestMethod]
    public void TailCall_CellValueReturnNeedsAnotherShape()
    {
        var session = new Session();
        Add(session, "ldc.i4.s -42", "tail.", "call int32 [System.Runtime]System.Math::Abs(int32)");
        var error = Assert.ThrowsExactly<ReplException>(() => session.Run());
        Assert.Contains("tail call must be followed by ret", error.Message);
    }

    /// <summary>
    /// An inline cell return cannot insert boxing between a tail call and its return.
    /// </summary>
    [TestMethod]
    public void TailCall_InlineCellReturnCannotBox()
    {
        var session = new Session();
        Add(session, "ldc.i4.0", "brtrue PENDING", "ldc.i4.s -42", "tail.",
            "call int32 [System.Runtime]System.Math::Abs(int32)");
        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine("ret"));
        Assert.Contains("must return object directly", error.Message);
        Assert.AreEqual("[int32]", session.State.StackText);
    }

    /// <summary>
    /// An inline cell return cannot synthesize null after a void tail call.
    /// </summary>
    [TestMethod]
    public void TailCall_InlineCellReturnCannotAddNull()
    {
        var session = new Session();
        Add(session, "ldc.i4.0", "brtrue PENDING", "tail.", "call void [System.Console]System.Console::WriteLine()");
        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine("ret"));
        Assert.Contains("must return object directly", error.Message);
        Assert.AreEqual("[]", session.State.StackText);
    }

    /// <summary>
    /// An inline cell return may directly return a tail call's reference result.
    /// </summary>
    [TestMethod]
    public void TailCall_InlineCellReferenceReturnRuns()
    {
        var session = new Session();
        Add(session, "ldc.i4.0", "brtrue PENDING", "ldstr \"forty\"", "ldstr \"two\"", "tail.",
            "call string string::Concat(string, string)", "ret", "PENDING: ldstr \"fallback\"");
        Assert.AreEqual("fortytwo", session.Run().Value);
    }

    /// <summary>
    /// A typed unmanaged pointer selects fields from its element and yields native addresses.
    /// </summary>
    [TestMethod]
    public void FieldReceiver_TypedPointerRequiresItsElementType()
    {
        var session = new Session();
        Add(session, ".method int32 ReadPointer(int32 value) {",
            ".locals init (valuetype [System.Runtime]System.ValueTuple`1<int32>* pointer)",
            "ldarga.s value", "conv.u", "stloc pointer", "ldloc pointer",
            "ldfld !0 valuetype [System.Runtime]System.ValueTuple`1<int32>::Item1", "ret", "}");
        Add(session, "ldc.i4.s 42", "call int32 ReadPointer(int32)");
        Assert.AreEqual(42, session.Run().Value);

        Add(session, ".method int32 AddressPointer(int32 value) {",
            ".locals init (valuetype [System.Runtime]System.ValueTuple`1<int32>* pointer)",
            "ldarga.s value", "conv.u", "stloc pointer", "ldloc pointer",
            "ldflda !0 valuetype [System.Runtime]System.ValueTuple`1<int32>::Item1");
        Assert.AreEqual("[native int]", session.State.StackText);
        Add(session, "ldind.i4", "ret", "}", "ldc.i4.s 42", "call int32 AddressPointer(int32)");
        Assert.AreEqual(42, session.Run().Value);

        Add(session, ".method int32 WrongPointer(int32 value) {",
            ".locals init (valuetype [System.Runtime]System.ValueTuple`1<int64>* pointer)",
            "ldarga.s value", "conv.u", "stloc pointer", "ldloc pointer");
        var error = Assert.ThrowsExactly<ReplException>(() =>
            session.AddLine("ldfld !0 valuetype [System.Runtime]System.ValueTuple`1<int32>::Item1"));
        Assert.Contains("receiver", error.Message);
    }

    /// <summary>
    /// A native-integer field receiver is accepted and ldflda retains an unmanaged address on the stack.
    /// </summary>
    [TestMethod]
    public void FieldReceiver_NativeIntegerRetainsNativeAddress()
    {
        var session = new Session();
        Add(session, ".method int32 AddressNative(int32 value) {", "ldarga.s value", "conv.u",
            "ldflda !0 valuetype [System.Runtime]System.ValueTuple`1<int32>::Item1");
        Assert.AreEqual("[native int]", session.State.StackText);
        Add(session, "ldind.i4", "ret", "}", "ldc.i4.s 42", "call int32 AddressNative(int32)");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// A jmp cannot transfer control from any protected region.
    /// </summary>
    [TestMethod]
    public void JumpFromTry_IsRejected()
    {
        var session = new Session();
        Add(session, ".method int32 Jump(int32 value) {", ".try {");
        var error = Assert.ThrowsExactly<ReplException>(() =>
            session.AddLine("jmp int32 [System.Runtime]System.Math::Abs(int32)"));
        Assert.Contains("jmp is not allowed inside a protected region", error.Message);
    }

    /// <summary>
    /// Stack allocation is refused as soon as it is entered in every exception-handler section.
    /// </summary>
    /// <param name="section">The handler section containing the allocation.</param>
    [TestMethod]
    [DataRow("catch")]
    [DataRow("filter")]
    [DataRow("filter handler")]
    [DataRow("finally")]
    [DataRow("fault")]
    public void LocallocInExceptionHandler_IsRejectedImmediately(string section)
    {
        var session = new Session();
        Add(session, ".method void Allocate() {", ".try {", "leave DONE");
        if (section == "filter handler")
        {
            Add(session, "} filter {", "pop", "ldc.i4.1", "endfilter", "} handler {");
        }
        else
        {
            Add(session, section switch
            {
                "catch" => "} catch [System.Runtime]System.Exception {",
                "filter" => "} filter {",
                "finally" => "} finally {",
                _ => "} fault {",
            });
        }

        if (section is "catch" or "filter" or "filter handler")
        {
            Add(session, "pop");
        }

        Add(session, "ldc.i4.4");
        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine("localloc"));
        Assert.Contains("localloc is not allowed inside an exception handler", error.Message);
        Assert.AreEqual("[int32]", session.State.StackText, "the refused instruction must not change the open handler");
    }

    /// <summary>
    /// An outer finalizer does not make endfinally valid inside a nested non-finalizer region.
    /// </summary>
    /// <param name="section">The nested region containing endfinally.</param>
    [TestMethod]
    [DataRow("try")]
    [DataRow("catch")]
    [DataRow("filter")]
    [DataRow("filter handler")]
    public void EndfinallyInNestedNonFinalizer_IsRejectedImmediately(string section)
    {
        var session = new Session();
        Add(session, ".method void Nested() {", ".try {", "leave DONE", "} finally {", ".try {");
        if (section != "try")
        {
            Add(session, "ldnull", "throw", section == "catch"
                ? "} catch [System.Runtime]System.Exception {" : "} filter {", "pop");
        }

        if (section == "filter handler")
        {
            Add(session, "ldc.i4.1", "endfilter", "} handler {", "pop");
        }

        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine("endfinally"));
        Assert.Contains("endfinally is only valid inside finally or fault", error.Message);
    }

    /// <summary>
    /// Endfinally remains valid when the innermost region is a finally or fault handler.
    /// </summary>
    /// <param name="section">The finalizer section containing endfinally.</param>
    [TestMethod]
    [DataRow("finally")]
    [DataRow("fault")]
    public void EndfinallyInInnermostFinalizer_IsAccepted(string section)
    {
        var session = new Session();
        Add(session, ".method void Nested() {", ".try {", ".try {", "ldnull", "throw", $"}} {section} {{",
            "endfinally", "}", "leave DONE", "} catch [System.Runtime]System.Exception {", "pop", "leave DONE",
            "}", "DONE: ret", "}", "call void Nested()");
        Assert.IsTrue(session.Run().IsVoid);
    }

    /// <summary>
    /// An outer catch does not make rethrow valid inside a nested finally or fault handler.
    /// </summary>
    /// <param name="section">The nested handler containing rethrow.</param>
    [TestMethod]
    [DataRow("finally")]
    [DataRow("fault")]
    public void RethrowInNestedFinalizer_IsRejectedImmediately(string section)
    {
        var session = new Session();
        Add(session, ".method void Nested() {", ".try {", "ldnull", "throw",
            "} catch [System.Runtime]System.Exception {", "pop", ".try {", "leave INNER", $"}} {section} {{");
        var error = Assert.ThrowsExactly<ReplException>(() => session.AddLine("rethrow"));
        Assert.Contains("rethrow is only valid inside a catch handler", error.Message);
    }

    /// <summary>
    /// Rethrow remains valid in a nested try whose enclosing handler is a catch.
    /// </summary>
    [TestMethod]
    public void RethrowInNestedTryWithinCatch_IsAccepted()
    {
        var session = new Session();
        Add(session, ".method void Nested() {", ".try {", "ldnull", "throw",
            "} catch [System.Runtime]System.Exception {", "pop", ".try {", "rethrow",
            "} catch [System.Runtime]System.Exception {", "pop", "leave DONE", "}", "}", "DONE: ret", "}",
            "call void Nested()");
        Assert.IsTrue(session.Run().IsVoid);
    }

    /// <summary>
    /// A manually entered jump cannot reinterpret the enclosing method's parameters.
    /// </summary>
    [TestMethod]
    public void JumpTarget_MustMatchTheCurrentParameters()
    {
        var session = new Session();
        Add(session, ".method void Jump() {");
        var error = Assert.ThrowsExactly<ReplException>(() =>
            session.AddLine("jmp int32 [System.Runtime]System.Math::Abs(int32)"));
        Assert.Contains("jmp target must match", error.Message);
    }

    /// <summary>
    /// A jump cannot leave the implicit protected region of a synchronized method.
    /// </summary>
    [TestMethod]
    public void JumpFromSynchronizedMethod_IsRejected()
    {
        var session = new Session();
        Add(session, ".class public JumpHost {",
            ".method public static int32 Jump(int32 value) cil managed synchronized {", "ldarg value", "pop");
        var error = Assert.ThrowsExactly<ReplException>(() =>
            session.AddLine("jmp int32 [System.Runtime]System.Math::Abs(int32)"));
        Assert.Contains("jmp is not allowed in a synchronized method", error.Message);
    }

    private static void Add(Session session, params string[] lines)
    {
        foreach (var line in lines)
        {
            session.AddLine(line);
        }
    }
}
