using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="Session.Mark"/> and <see cref="Session.Rollback"/>: a block a line of
/// which was refused is withdrawn whole, everything before it stays, and nothing is re-run.
/// </summary>
[TestClass]
public sealed class SessionMarkTests
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

    private static void Refuse(Session session, string line) => Assert.ThrowsExactly<ReplException>(() => session.AddLine(line));

    /// <summary>
    /// A method opened after the mark is abandoned.
    /// </summary>
    [TestMethod]
    public void Rollback_MethodOpenedAfterMark_Abandons()
    {
        var session = Load("ldc.i4 1");
        var mark = session.Mark();
        session.AddLine(".method int32 F() {");
        session.AddLine("ldc.i4 1");
        Refuse(session, "lcd.i4 2");
        Assert.IsTrue(session.Rollback(mark));
        Assert.IsNull(session.OpenMethod);
        Assert.AreEqual(1, session.State.Stack.Count);
        Assert.AreEqual(mark, session.Mark());
    }

    /// <summary>
    /// A method already open at the mark is cut back to the lines it had, and its state replayed.
    /// </summary>
    [TestMethod]
    public void Rollback_MethodBodyGrewAfterMark_TruncatesAndReplays()
    {
        var session = Load(".method int32 F(int32 n) {", "ldarg n");
        var mark = session.Mark();
        session.AddLine("ldc.i4 2");
        session.AddLine("mul");
        Refuse(session, "ldarg missing");
        Assert.IsTrue(session.Rollback(mark));
        Assert.AreEqual("F", session.OpenMethod?.Name);
        Assert.AreEqual(1, session.State.Stack.Count);
        Assert.AreEqual(1, session.State.InstructionCount);
        session.AddLine("ret");
        session.AddLine("}");
        Assert.HasCount(1, session.Methods);
    }

    /// <summary>
    /// A closing brace refused because the body leaves the wrong return type leaves the method
    /// open today; a rollback takes it away so the whole declaration can be sent again.
    /// </summary>
    [TestMethod]
    public void Rollback_CloseTimeFailure_LeavesNoOpenMethod()
    {
        var session = new Session();
        var mark = session.Mark();
        session.AddLine(".method int32 F() {");
        session.AddLine("ldstr \"wrong return\"");
        Refuse(session, "}");
        Assert.AreEqual("F", session.OpenMethod?.Name);
        Assert.IsTrue(session.Rollback(mark));
        Assert.IsNull(session.OpenMethod);
        Assert.IsEmpty(session.Methods);

        session.AddLine(".method int32 F() {");
        session.AddLine("ldc.i4 1");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.HasCount(1, session.Methods);
        session.AddLine("call int32 F()");
        Assert.AreEqual(1, session.Run().Value);
    }

    /// <summary>
    /// A class opened after the mark, method inside it and all, is gone after the rollback.
    /// </summary>
    [TestMethod]
    public void Rollback_NestedMethodInsideClass_RestoresFamily()
    {
        var session = new Session();
        var mark = session.Mark();
        session.AddLine(".class public C {");
        session.AddLine(".field public int32 X");
        session.AddLine(".method public static int32 One() {");
        session.AddLine("ldc.i4 1");
        Refuse(session, "lcd.i4 2");
        Assert.IsTrue(session.Rollback(mark));
        Assert.IsNull(session.OpenType);
        Assert.IsNull(session.OpenMethod);
        Assert.IsEmpty(session.Types);
    }

    /// <summary>
    /// A member accepted before the mark survives a rollback of a member refused after it, and
    /// the family still closes and runs.
    /// </summary>
    [TestMethod]
    public void Rollback_FamilyWithAcceptedMember_ReplaysMember()
    {
        var session = Load(".class public C {", ".method public static int32 One() {", "ldc.i4 1", "ret", "}");
        var mark = session.Mark();
        session.AddLine(".method public static int32 Two() {");
        session.AddLine("ldc.i4 2");
        Refuse(session, "lcd.i4 2");
        Assert.IsTrue(session.Rollback(mark));
        Assert.AreEqual("C", session.OpenType);
        Assert.IsNull(session.OpenMethod);
        session.AddLine("}");
        Assert.HasCount(1, session.Types);
        session.AddLine("call int32 C::One()");
        Assert.AreEqual(1, session.Run().Value);
    }

    /// <summary>
    /// A protected region typed into the cell after the mark is withdrawn with its lines, and the
    /// region can be opened again without nesting.
    /// </summary>
    [TestMethod]
    public void Rollback_TryRegionOpenedAfterMark_RestoresDepthZero()
    {
        var session = new Session();
        var mark = session.Mark();
        session.AddLine(".try {");
        session.AddLine("nop");
        Refuse(session, "lcd.i4 1");
        Assert.AreEqual(1, session.State.OpenBlockDepth);
        Assert.IsTrue(session.Rollback(mark));
        Assert.AreEqual(0, session.State.OpenBlockDepth);
        Assert.IsEmpty(session.BodyLines);
        session.AddLine(".try {");
        Assert.AreEqual(1, session.State.OpenBlockDepth);
        Assert.AreEqual(1, session.OpenDepth);
    }

    /// <summary>
    /// Everything before the mark stays: the locals, the instructions, and the value on the stack.
    /// </summary>
    [TestMethod]
    public void Rollback_TryRegion_KeepsEarlierLocalsAndBody()
    {
        var session = Load(".locals init (int32 i)", "ldc.i4 1");
        var mark = session.Mark();
        session.AddLine(".try {");
        session.AddLine("nop");
        Refuse(session, "lcd.i4 1");
        Assert.IsTrue(session.Rollback(mark));
        Assert.AreEqual(1, session.State.Stack.Count);
        Assert.HasCount(1, session.State.Locals);
        Assert.HasCount(1, session.BodyLines);
        Assert.HasCount(1, session.DeclarationLines);
        Assert.AreEqual(0, session.State.OpenBlockDepth);
        Assert.AreEqual(1, session.Run().Value);
    }

    /// <summary>
    /// A mark taken inside an open region cuts the region back to the lines it had.
    /// </summary>
    [TestMethod]
    public void Rollback_InsideOpenRegion_TruncatesToMark()
    {
        var session = Load(".try {", "nop");
        var mark = session.Mark();
        session.AddLine("nop");
        session.AddLine("nop");
        Refuse(session, "lcd.i4 1");
        Assert.IsTrue(session.Rollback(mark));
        Assert.HasCount(2, session.BodyLines);
        Assert.AreEqual(1, session.State.OpenBlockDepth);
        Assert.AreEqual(1, session.State.InstructionCount);
    }

    /// <summary>
    /// A refused catch header withdraws the whole region, try body included.
    /// </summary>
    [TestMethod]
    public void Rollback_CatchHeaderRefused_RestoresTryBody()
    {
        var session = Load("ldc.i4 7");
        var mark = session.Mark();
        session.AddLine(".try {");
        session.AddLine("nop");
        session.AddLine("leave END");
        Refuse(session, "} catch NoSuchTypeAnywhere {");
        Assert.IsTrue(session.Rollback(mark));
        Assert.AreEqual(0, session.State.OpenBlockDepth);
        Assert.HasCount(1, session.BodyLines);
        Assert.AreEqual(1, session.State.Stack.Count);
    }

    /// <summary>
    /// A rollback returns the comment state the mark was taken in.
    /// </summary>
    [TestMethod]
    public void Rollback_RestoresCommentState()
    {
        var session = new Session();
        var mark = session.Mark();
        session.AddLine(".method int32 F() {");
        session.AddLine("/* open");
        Assert.IsTrue(session.InBlockComment);
        Assert.IsTrue(session.Rollback(mark));
        Assert.IsFalse(session.InBlockComment);
        Assert.IsNull(session.OpenMethod);
    }

    /// <summary>
    /// Rolling back twice to the same mark is harmless.
    /// </summary>
    [TestMethod]
    public void Rollback_Twice_IsIdempotent()
    {
        var session = Load("ldc.i4 1");
        var mark = session.Mark();
        session.AddLine(".try {");
        session.AddLine("nop");
        Assert.IsTrue(session.Rollback(mark));
        Assert.IsTrue(session.Rollback(mark));
        Assert.AreEqual(mark, session.Mark());
        Assert.HasCount(1, session.BodyLines);
    }

    /// <summary>
    /// A mark taken before a same-signature replacement is stale once the replacement commits.
    /// </summary>
    [TestMethod]
    public void Rollback_StaleAfterReplacement_Refused()
    {
        var session = Load(".method int32 F() {", "ldc.i4 1", "ret", "}");
        var mark = session.Mark();
        session.AddLine(".method int32 F() {");
        session.AddLine("ldc.i4 2");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.IsFalse(session.Rollback(mark));
        Assert.HasCount(1, session.Methods);
        session.AddLine("call int32 F()");
        Assert.AreEqual(2, session.Run().Value);
    }

    /// <summary>
    /// A run, an undo, or bound type parameters after the mark make it stale, and nothing is undone.
    /// </summary>
    [TestMethod]
    public void Rollback_AfterRunUndoOrTypeParams_Refused()
    {
        var session = Load("ldc.i4 1");
        var mark = session.Mark();
        session.AddLine("ldc.i4 2");
        session.AddLine("add");
        Assert.AreEqual(3, session.Run().Value);
        Assert.IsFalse(session.Rollback(mark));
        Assert.AreEqual(1, session.CellsRun);

        session.AddLine("ldc.i4 3");
        mark = session.Mark();
        session.AddLine("ldc.i4 4");
        session.Undo();
        Assert.IsFalse(session.Rollback(mark));
        Assert.HasCount(1, session.BodyLines);

        session.ClearCell();
        mark = session.Mark();
        session.AddLine(".typeparams (T)");
        Assert.IsFalse(session.Rollback(mark));
        Assert.HasCount(1, session.TypeParameterNames);
    }

    /// <summary>
    /// The open depth counts regions, methods, members, and type blocks together.
    /// </summary>
    [TestMethod]
    public void OpenDepth_CountsEveryOpenBrace()
    {
        var session = new Session();
        Assert.AreEqual(0, session.OpenDepth);
        session.AddLine(".class public Outer {");
        Assert.AreEqual(1, session.OpenDepth);
        session.AddLine(".class nested public Inner {");
        Assert.AreEqual(2, session.OpenDepth);
        session.AddLine(".method public static void M() {");
        Assert.AreEqual(3, session.OpenDepth);
        session.AddLine(".try {");
        Assert.AreEqual(4, session.OpenDepth);
        session.AbandonType();
        Assert.AreEqual(0, session.OpenDepth);
        session.AddLine(".method int32 F() {");
        Assert.AreEqual(1, session.OpenDepth);
    }

    /// <summary>
    /// A mark taken while a method header still waits for its brace puts the method back to
    /// waiting: after the rollback the depth is zero again and the brace is accepted once.
    /// </summary>
    [TestMethod]
    public void Rollback_HeaderWithoutBrace_RestoresTheWaitingBrace()
    {
        var session = new Session();
        session.AddLine(".method int32 One()");
        var mark = session.Mark();
        Assert.IsFalse(mark.BraceSeen);
        Assert.AreEqual(0, session.OpenDepth);
        session.AddLine("{");
        session.AddLine("ldc.i4 1");
        Assert.AreEqual(1, session.OpenDepth);
        Assert.IsTrue(session.Rollback(mark));
        Assert.AreEqual(0, session.OpenDepth, "the brace is waited for again");
        Assert.IsNotNull(session.OpenMethod);
        session.AddLine("{");
        Assert.AreEqual(1, session.OpenDepth);
        session.AddLine("ldc.i4 1");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.IsNull(session.OpenMethod);
        Assert.HasCount(1, session.Methods);

        // The brace alone, with no body line after it, is taken back the same way.
        session.AddLine(".method int32 Two()");
        var waiting = session.Mark();
        session.AddLine("{");
        Assert.AreEqual(1, session.OpenDepth);
        Assert.IsTrue(session.Rollback(waiting));
        Assert.AreEqual(0, session.OpenDepth);
        session.AddLine("{");
        session.AddLine("ldc.i4 2");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.HasCount(2, session.Methods);

        // A mark taken after the brace keeps it.
        session.AddLine(".method int32 Three()");
        session.AddLine("{");
        var seen = session.Mark();
        Assert.IsTrue(seen.BraceSeen);
        session.AddLine("ldc.i4 3");
        Assert.IsTrue(session.Rollback(seen));
        Assert.AreEqual(1, session.OpenDepth, "the brace stays seen");
        session.AddLine("ldc.i4 3");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.HasCount(3, session.Methods);
    }

    /// <summary>
    /// A member whose brace came on its own line is replayed with that brace: a rollback inside
    /// the member keeps the class and puts the member back to where the mark stood.
    /// </summary>
    [TestMethod]
    public void Rollback_MemberBraceOnItsOwnLine_ReplaysTheBrace()
    {
        var session = new Session();
        session.AddLine(".class public C {");
        session.AddLine(".method public static int32 M()");
        Assert.AreEqual(1, session.OpenDepth);
        var waiting = session.Mark();
        session.AddLine("{");
        session.AddLine("ldc.i4 1");
        Assert.AreEqual(2, session.OpenDepth);
        Assert.IsTrue(session.Rollback(waiting));
        Assert.AreEqual(1, session.OpenDepth, "the member waits for its brace again");
        Assert.IsNotNull(session.OpenType);
        session.AddLine("{");
        var seen = session.Mark();
        session.AddLine("ldc.i4 1");
        session.AddLine("ldc.i4 2");
        Assert.IsTrue(session.Rollback(seen));
        Assert.AreEqual(2, session.OpenDepth, "the brace stays seen and the body lines are gone");
        session.AddLine("ldc.i4 1");
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("}");
        Assert.IsNull(session.OpenType);
        Assert.AreEqual(1, session.TypeCount);

        // The brace alone is taken back the same way.
        session.AddLine(".class public D {");
        session.AddLine(".method public static int32 N()");
        var before = session.Mark();
        session.AddLine("{");
        Assert.IsTrue(session.Rollback(before));
        Assert.AreEqual(1, session.OpenDepth);
        session.AddLine("{");
        session.AddLine("ldc.i4 3");
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("}");
        Assert.AreEqual(2, session.TypeCount);
    }
}
