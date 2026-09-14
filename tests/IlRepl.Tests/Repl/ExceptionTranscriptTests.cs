using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Repl;
using IlRepl.Tests.Engine;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Real nested and cyclic exceptions remain understandable in both invocation and scenario reports.
/// </summary>
[TestClass]
public sealed class ExceptionTranscriptTests
{
    /// <summary>
    /// Supplies cancellation for the real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The transcript exposes differing inner messages and observation problems even when a scenario catches the exception.
    /// </summary>
    /// <param name="caught">Whether the scenario handles the exception.</param>
    /// <param name="cycle">Whether the revised exception chain is cyclic.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ReportComparison_RendersNestedExceptionsAndProblems(bool caught, bool cycle)
    {
        var session = IlLines.Load(ExceptionChainExamples.Method(cycle, false).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ExceptionChainExamples.Method(cycle, true));
        foreach (var line in ExceptionChainExamples.Scenario(caught).Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual(cycle ? "incomplete" : "different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        var core = new ReplCore();
        core.ReportComparison(result);
        var text = string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
        foreach (var (side, message) in new[] { (result.Original, "original detail"), (result.Edited, "edited detail") })
        {
            Assert.AreEqual(caught, side.Exception is null);
            var exception = side.Invocations.Single().Exception!;
            Assert.AreEqual("outer failure", exception.Message);
            Assert.AreEqual("middle failure", exception.Inner!.Message);
            Assert.AreEqual(message, exception.Inner.Inner!.Message);
            Assert.AreEqual(cycle && side == result.Edited, exception.Inner.Inner.Inner?.Problem is not null);
            Assert.Contains("call 1 threw [System.Private.CoreLib]System.Exception: outer failure", text);
            Assert.Contains("caused by [System.Private.CoreLib]System.InvalidOperationException: middle failure", text);
            var leaf = "caused by [System.Private.CoreLib]System.ArgumentException: " + message;
            Assert.Contains(leaf, text);
            Assert.HasCount(caught ? 1 : 2, core.Transcript.Lines.Where(line => line.PlainText.Contains(leaf, StringComparison.Ordinal)));
            if (cycle && side == result.Edited)
            {
                Assert.Contains("unavailable: exception chain is cyclic or exceeds the observation depth limit", text);
            }
        }
    }
}
