using IlRepl.Engine;
using IlRepl.Repl;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="Session.CompletionRevision"/>: it moves on everything a completion could
/// depend on, including changes equal statuses cannot show.
/// </summary>
[TestClass]
public sealed class SessionRevisionTests
{
    /// <summary>
    /// Declaring, rolling back, and declaring again are three revisions, while the generation stays.
    /// </summary>
    [TestMethod]
    public void Revision_ChangesOnEveryMutationIncludingRollback()
    {
        var session = new Session();
        var start = session.CompletionRevision;
        var generation = session.Generation;
        var mark = session.Mark();
        session.AddLine(".args (int32 a = 1)");
        var declared = session.CompletionRevision;
        Assert.AreNotEqual(start, declared);
        Assert.IsTrue(session.Rollback(mark));
        var rolledBack = session.CompletionRevision;
        Assert.AreNotEqual(declared, rolledBack);
        session.AddLine(".args (int32 b = 2)");
        var declaredAgain = session.CompletionRevision;
        Assert.AreNotEqual(rolledBack, declaredAgain);
        Assert.AreEqual(generation, session.Generation, "a rollback is not a generation");
        Assert.HasCount(3, new[] { declared, rolledBack, declaredAgain }.Distinct().ToList());
    }

    /// <summary>
    /// A line that only opens or closes a comment changes the revision, though nothing else moves.
    /// </summary>
    [TestMethod]
    public void Revision_ChangesOnACommentOnlyLine()
    {
        var core = new ReplCore();
        var before = core.Session.CompletionRevision;
        var status = core.Status;
        Assert.IsTrue(core.Handle("/*").Succeeded);
        var opened = core.Session.CompletionRevision;
        Assert.AreNotEqual(before, opened);
        Assert.IsTrue(core.Handle("*/").Succeeded);
        var closed = core.Session.CompletionRevision;
        Assert.AreNotEqual(opened, closed);
        Assert.AreEqual(status with { Revision = closed }, core.Status, "nothing but the revision moved");
        Assert.AreEqual(closed, core.Status.Revision);
    }

    /// <summary>
    /// A refused line that opened a comment puts the comment state back and moves the revision, so
    /// a preview that saw the open comment does not survive.
    /// </summary>
    [TestMethod]
    public void Revision_ChangesWhenARefusedLineRestoresTheCommentState()
    {
        var core = new ReplCore();
        var before = core.Session.CompletionRevision;
        Assert.IsFalse(core.Handle("lcd.i4 2 /*").Succeeded);
        Assert.IsFalse(core.Session.InBlockComment, "the refused line's comment does not stay open");
        Assert.AreNotEqual(before, core.Session.CompletionRevision);
    }

    /// <summary>
    /// Every structural operation moves the revision: run, undo, clear, reset, a commit, and a load.
    /// </summary>
    [TestMethod]
    public void Revision_ChangesOnRunUndoClearResetCommitAndLoad()
    {
        var core = new ReplCore();
        var session = core.Session;
        var revisions = new List<long> { session.CompletionRevision };
        void Step(string line)
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line);
            revisions.Add(session.CompletionRevision);
        }

        Step("ldc.i4 1");
        Step(".undo");
        Step("ldc.i4 2");
        Step("ret");
        core.Handle(".method int32 One() {");
        core.Handle("ldc.i4 1");
        core.Handle("ret");
        Step("}");
        core.Handle("ldc.i4 3");
        Step(".clear");
        Step(".load " + SampleHost.Samples.GreeterDll);
        Step(".reset");
        Assert.HasCount(revisions.Count, revisions.Distinct().ToList(), string.Join(", ", revisions));
    }
}
