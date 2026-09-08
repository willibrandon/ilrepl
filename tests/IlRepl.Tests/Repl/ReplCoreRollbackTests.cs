using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Tests for <see cref="ReplCore.Rollback"/>: the note it leaves and the status it returns to.
/// </summary>
[TestClass]
public sealed class ReplCoreRollbackTests
{
    private static string Plain(ReplCore core) => string.Join("\n", core.Transcript.Lines.Select(l => l.PlainText));

    /// <summary>
    /// After a refused closing brace the method is abandoned and the note says so.
    /// </summary>
    [TestMethod]
    public void Rollback_AfterRefusedClose_AbandonsMethod()
    {
        var core = new ReplCore();
        var mark = core.Status.Mark;
        core.Handle(".method int32 F() {");
        core.Handle("ldstr \"wrong\"");
        Assert.IsFalse(core.Handle("}").Succeeded);
        Assert.AreEqual("F", core.Status.OpenMethod);

        core.Transcript.Clear();
        Assert.IsTrue(core.Rollback(mark).Succeeded);
        Assert.Contains("method F abandoned; the block is back in the editor", Plain(core));
        Assert.IsNull(core.Status.OpenMethod);
        Assert.AreEqual(0, core.Status.OpenDepth);
        Assert.AreEqual(mark, core.Status.Mark);
    }

    /// <summary>
    /// A refused line in a class withdraws the class; a refused line in a region withdraws the lines.
    /// </summary>
    [TestMethod]
    public void Rollback_ClassAndRegion_NoteWhatWentAway()
    {
        var core = new ReplCore();
        var mark = core.Status.Mark;
        core.Handle(".class public C {");
        core.Handle(".field public int32 X");
        Assert.IsFalse(core.Handle(".field public int32 X").Succeeded);
        core.Transcript.Clear();
        Assert.IsTrue(core.Rollback(mark).Succeeded);
        Assert.Contains("class C abandoned", Plain(core));
        Assert.IsNull(core.Status.OpenType);

        core.Handle("ldc.i4 1");
        mark = core.Status.Mark;
        core.Handle(".try {");
        core.Handle("nop");
        Assert.IsFalse(core.Handle("lcd.i4 1").Succeeded);
        core.Transcript.Clear();
        Assert.IsTrue(core.Rollback(mark).Succeeded);
        Assert.Contains("lines withdrawn; the block is back in the editor", Plain(core));
        Assert.AreEqual(0, core.Status.OpenBlocks);
        Assert.AreEqual(1, core.Status.StackDepth);
    }

    /// <summary>
    /// A mark from before a run is stale; the note says why nothing was withdrawn.
    /// </summary>
    [TestMethod]
    public void Rollback_StaleMark_NotesWhy()
    {
        var core = new ReplCore();
        var mark = core.Status.Mark;
        core.Handle("ldc.i4 1");
        core.Handle("ret");
        core.Transcript.Clear();
        Assert.IsFalse(core.Rollback(mark).Succeeded);
        Assert.Contains("nothing withdrawn", Plain(core));
        Assert.AreEqual(2, core.Status.CellNumber);
    }

    /// <summary>
    /// The in-process engine reports the rollback like any other reply.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task InProcessEngine_Rollback_ReturnsLinesAndStatus()
    {
        await using var engine = new InProcessEngine();
        var mark = engine.Status.Mark;
        await engine.HandleAsync(".method int32 F() {", CancellationToken.None);
        await engine.HandleAsync("ldc.i4 1", CancellationToken.None);
        var reply = await engine.RollbackAsync(mark, CancellationToken.None);
        Assert.IsTrue(reply.Succeeded);
        Assert.Contains(l => l.Kind == LineKind.Info && l.PlainText.Contains("method F abandoned", StringComparison.Ordinal), reply.Lines);
        Assert.IsNull(reply.Status.OpenMethod);
        Assert.AreEqual(reply.Status, engine.Status);
    }
}
