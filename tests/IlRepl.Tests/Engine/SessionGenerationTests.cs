using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="Session.Generation"/>: it moves on every change that forgetting lines
/// cannot undo, and stays put while lines are merely accepted.
/// </summary>
[TestClass]
public sealed class SessionGenerationTests
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

    /// <summary>
    /// A run advances the generation.
    /// </summary>
    [TestMethod]
    public void Generation_Run_Advances()
    {
        var session = Load("ldc.i4 1");
        var before = session.Generation;
        session.Run();
        Assert.AreNotEqual(before, session.Generation);
    }

    /// <summary>
    /// A cell that throws still ran, so the generation advances.
    /// </summary>
    [TestMethod]
    public void Generation_RunThatThrows_Advances()
    {
        var session = Load("ldnull", "throw");
        var before = session.Generation;
        Assert.ThrowsExactly<CellException>(() => session.Run());
        Assert.AreNotEqual(before, session.Generation);
    }

    /// <summary>
    /// Replacing a method with the same signature commits a new body, so the generation advances
    /// although the method count does not.
    /// </summary>
    [TestMethod]
    public void Generation_SameSignatureReplacement_Advances()
    {
        var session = Load(".method int32 F() {", "ldc.i4 1", "ret", "}");
        var before = session.Generation;
        session.AddLine(".method int32 F() {");
        session.AddLine("ldc.i4 2");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.HasCount(1, session.Methods);
        Assert.AreNotEqual(before, session.Generation);
    }

    /// <summary>
    /// A class family commits at its outermost brace.
    /// </summary>
    [TestMethod]
    public void Generation_FamilyCommit_Advances()
    {
        var session = Load(".class public C {", ".field public int32 X");
        var before = session.Generation;
        session.AddLine("}");
        Assert.AreNotEqual(before, session.Generation);
    }

    /// <summary>
    /// Undo, clear, reset, and abandon discard lines a mark could not bring back.
    /// </summary>
    [TestMethod]
    public void Generation_UndoClearResetAbandon_Advance()
    {
        var session = Load("ldc.i4 1", "ldc.i4 2");
        var g = session.Generation;
        Assert.IsTrue(session.Undo());
        Assert.AreNotEqual(g, session.Generation);

        g = session.Generation;
        session.ClearCell();
        Assert.AreNotEqual(g, session.Generation);

        session.AddLine(".method int32 F() {");
        g = session.Generation;
        Assert.IsTrue(session.AbandonMethod());
        Assert.AreNotEqual(g, session.Generation);

        session.AddLine(".class public C {");
        g = session.Generation;
        Assert.IsTrue(session.AbandonType());
        Assert.AreNotEqual(g, session.Generation);

        g = session.Generation;
        session.Reset();
        Assert.AreNotEqual(g, session.Generation);

        // Nothing to undo changes nothing.
        g = session.Generation;
        Assert.IsFalse(session.Undo());
        Assert.AreEqual(g, session.Generation);
    }

    /// <summary>
    /// Lines accepted into the cell, an open method, or an open class are provisional and leave
    /// the generation alone.
    /// </summary>
    [TestMethod]
    public void Generation_ProvisionalLines_DoNotAdvance()
    {
        var session = new Session();
        var g = session.Generation;
        session.AddLine(".locals init (int32 i)");
        session.AddLine("ldc.i4 1");
        session.AddLine("stloc i");
        session.AddLine(".method int32 F() {");
        session.AddLine("ldc.i4 1");
        session.AddLine("ret");
        Assert.AreEqual(g, session.Generation);
        session.AbandonMethod();
        g = session.Generation;
        session.AddLine(".class public C {");
        session.AddLine(".field public int32 X");
        session.AddLine(".method public static int32 One() {");
        session.AddLine("ldc.i4 1");
        session.AddLine("ret");
        session.AddLine("}");
        Assert.AreEqual(g, session.Generation);
    }

    /// <summary>
    /// A protected region typed into the cell is provisional too.
    /// </summary>
    [TestMethod]
    public void Generation_TryRegionLines_DoNotAdvance()
    {
        var session = new Session();
        var g = session.Generation;
        session.AddLine(".try {");
        session.AddLine("nop");
        session.AddLine("leave END");
        session.AddLine("} catch [System.Runtime]System.Exception {");
        session.AddLine("pop");
        session.AddLine("leave END");
        session.AddLine("}");
        Assert.AreEqual(g, session.Generation);
    }

    /// <summary>
    /// Bound type parameters and arguments cannot be forgotten by dropping lines.
    /// </summary>
    [TestMethod]
    public void Generation_TypeParamsAndArgs_Advance()
    {
        var session = new Session();
        var g = session.Generation;
        session.AddLine(".typeparams (T)");
        Assert.AreNotEqual(g, session.Generation);
        g = session.Generation;
        session.AddLine(".typeargs (int32)");
        Assert.AreNotEqual(g, session.Generation);
    }
}
