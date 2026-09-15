using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Relocated observation methods preserve receiver identity, mutation, dispatch, and null checks.
/// </summary>
[TestClass]
public sealed class ComparisonReceiverCallsTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Direct, virtual, constrained, and delegate calls mutate the same receiver and retain their null-call behavior.
    /// </summary>
    /// <param name="kind">The selected invocation operation.</param>
    /// <param name="valueType">Whether the receiver is a struct.</param>
    /// <param name="generic">Whether the owner is generic.</param>
    [TestMethod]
    [DataRow("call", false, false)]
    [DataRow("call", true, false)]
    [DataRow("virtual", false, false)]
    [DataRow("virtual", false, true)]
    [DataRow("constrained", false, false)]
    [DataRow("constrained", true, false)]
    [DataRow("constrained", false, true)]
    [DataRow("constrained", true, true)]
    [DataRow("delegate", false, false)]
    [DataRow("delegate", false, true)]
    [DataRow("null-call", false, false)]
    [DataRow("null-virtual", false, false)]
    [DataRow("null-delegate", false, false)]
    public async Task Compare_RelocatedCallsPreserveReceiverSemantics(string kind, bool valueType, bool generic)
    {
        var session = IlLines.Load(ComparisonReceiverCallExamples.Source(valueType, generic).Split('\n'));
        var edit = session.PrepareEdit(ComparisonReceiverCallExamples.Reference(generic), "Copy");
        session.CommitEdit(edit.Name, ComparisonReceiverCallExamples.Method(generic, true));
        foreach (var line in ComparisonReceiverCallExamples.Scenario(kind, valueType, generic).Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var (side, value) in new[] { (result.Original, "8"), (result.Edited, "9") })
        {
            Assert.IsNull(side.Exception);
            Assert.AreEqual(value, side.Result!.Value);
            Assert.HasCount(kind == "null-call" ? 2 : 1, side.Invocations);
            var invocation = side.Invocations[^1];
            var input = invocation.Inputs.Single(member => member.Name == "receiver").Value;
            var output = invocation.Outputs.Single(member => member.Name == "receiver").Value;
            Assert.AreEqual("7", input.Members.Single(member => member.Name.EndsWith("::Value", StringComparison.Ordinal)).Value.Value);
            Assert.AreEqual(value, output.Members.Single(member => member.Name.EndsWith("::Value", StringComparison.Ordinal)).Value.Value);
            Assert.IsEmpty(invocation.Inputs.Where(member => member.Name.StartsWith("argument ", StringComparison.Ordinal)));
            if (kind == "null-call")
            {
                Assert.EndsWith("System.NullReferenceException", side.Invocations[0].Exception!.Type);
            }
        }
    }
}
