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

    private static void Add(Session session, params string[] lines)
    {
        foreach (var line in lines)
        {
            session.AddLine(line);
        }
    }
}
