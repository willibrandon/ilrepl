using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Comparison scenarios retain accessible framework originals whose runtime-owned helpers cannot be copied.
/// </summary>
[TestClass]
public sealed class ExternalOriginalScenarioTests
{
    /// <summary>
    /// The cancellation context for real worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The same scenario reaches the real framework original and the edited implementation in separate runtimes.
    /// </summary>
    /// <returns>The completed original and edited invocation assertions.</returns>
    [TestMethod]
    public async Task Compare_FrameworkOriginalSupportsScenarioCalls()
    {
        var session = new Session();
        var edit = session.PrepareEdit("int32 Math::Abs(int32)", "Copy");
        session.CommitEdit(edit.Name, ".method public static int32 Abs(int32 value) cil managed {\nldarg.0\nret\n}");
        foreach (var line in IlLines.Expand(".method int32 Scenario() { ldc.i4.s -42; call Copy; ret }"))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.AreEqual("completed", result.Edited.Outcome);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("-42", result.Edited.Result!.Value);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("-42", side.Invocations[0].Inputs.Single(member => member.Name == "argument 0").Value.Value);
        }

        Assert.AreEqual(42, edit.Original.Requested.Invoke(null, [-42]));
        Assert.AreEqual(-42, edit.Method!.Invoke(null, [-42]));
    }
}
