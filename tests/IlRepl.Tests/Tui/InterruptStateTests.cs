using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Verifies the operation and rendered-phase identities that authorize explicit runtime replacement.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class InterruptStateTests
{
    /// <summary>
    /// Repeated presses cannot escalate before a phase-specific notice is actually acknowledged.
    /// </summary>
    /// <param name="phase">The kind of operation being cancelled.</param>
    /// <param name="grace">The expected grace in milliseconds.</param>
    [TestMethod]
    [DataRow(ExecutionPhase.UserCode, 250)]
    [DataRow(ExecutionPhase.Cooperative, 5000)]
    [DataRow(ExecutionPhase.Cleanup, 5000)]
    public void Escalation_RequiresGraceAndMatchingRenderedNotice(ExecutionPhase phase, int grace)
    {
        var state = new InterruptState();
        var progress = new ExecutionProgress("first", 1, phase, true);
        state.Update(progress, TimeSpan.Zero);
        Assert.AreEqual(InterruptAction.Cancel, state.Press(TimeSpan.Zero, out _));
        var early = TimeSpan.FromMilliseconds(grace - 1);
        Assert.DoesNotContain("again", state.BuildNotice(early)!);
        Assert.AreEqual(InterruptAction.Consume, state.Press(early, out _));
        Assert.Contains("again", state.BuildNotice(TimeSpan.FromMilliseconds(grace))!);
        Assert.AreEqual(InterruptAction.Consume, state.Press(TimeSpan.FromMilliseconds(grace), out _));
        state.FrameFlushed("first", 0);
        Assert.AreEqual(InterruptAction.Consume, state.Press(TimeSpan.FromMilliseconds(grace), out _));
        state.FrameFlushed("first", 1);
        Assert.AreEqual(InterruptAction.Restart, state.Press(TimeSpan.FromMilliseconds(grace), out var observed));
        Assert.AreEqual(progress, observed);
    }

    /// <summary>
    /// Cleanup retracts an earlier short-window notice and settlement consumes repeated keys until editing resumes.
    /// </summary>
    [TestMethod]
    public void CleanupAndSettlement_RetractOldEscalation()
    {
        var state = new InterruptState();
        state.Update(new ExecutionProgress("first", 1, ExecutionPhase.UserCode, true), TimeSpan.Zero);
        Assert.AreEqual(InterruptAction.Cancel, state.Press(TimeSpan.Zero, out _));
        _ = state.BuildNotice(TimeSpan.FromMilliseconds(250));
        state.Update(new ExecutionProgress("first", 2, ExecutionPhase.Cleanup, true), TimeSpan.FromMilliseconds(251));
        state.FrameFlushed("first", 1);
        Assert.AreEqual(InterruptAction.Consume, state.Press(TimeSpan.FromMilliseconds(252), out _));
        Assert.StartsWith("Cancelling", state.BuildNotice(TimeSpan.FromMilliseconds(252))!);
        state.Update(new ExecutionProgress("first", 3, ExecutionPhase.Cleanup, false), TimeSpan.FromMilliseconds(253));
        Assert.AreEqual(InterruptAction.Consume, state.Press(TimeSpan.FromSeconds(10), out _));
        state.Edited();
        Assert.AreEqual(InterruptAction.None, state.Press(TimeSpan.FromSeconds(11), out _));
    }
}
