using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Repl;
using IlRepl.Tests.Engine;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Real process observations explain comparison differences in the session transcript.
/// </summary>
[TestClass]
public sealed class ComparisonTranscriptTests
{
    /// <summary>
    /// Supplies cancellation for isolated comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Shows both argument states, receiver mutations, and exceptions caught inside the scenario.
    /// </summary>
    /// <param name="kind">The observation that distinguishes the executions.</param>
    [TestMethod]
    [DataRow("receiver")]
    [DataRow("inputs")]
    [DataRow("exception")]
    public async Task ReportComparison_ShowsInvocationInputsAndExceptions(string kind)
    {
        var session = IlLines.Load(ComparisonTranscriptExamples.Source(kind).Split('\n'));
        var edit = session.PrepareEdit(ComparisonTranscriptExamples.Reference(kind), "Copy");
        session.CommitEdit(edit.Name, ComparisonTranscriptExamples.Method(kind, true));
        foreach (var line in ComparisonTranscriptExamples.Scenario(kind).Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);
        Assert.AreEqual(kind == "inputs" ? "different-inputs" : "different", result.Outcome,
            result.Original.Detail + "; " + result.Edited.Detail);
        var core = new ReplCore(session, new ReplOptions());
        core.ReportComparison(result);
        var text = string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
        const string scalar = "[System.Private.CoreLib]System.Int32";
        const string failure = "call 1 threw [System.Private.CoreLib]System.InvalidOperationException: ";
        if (kind == "exception")
        {
            Assert.IsNull(result.Original.Exception);
            Assert.IsNull(result.Edited.Exception);
            Assert.AreEqual("0", result.Original.Result!.Value);
            Assert.AreEqual("0", result.Edited.Result!.Value);
            Assert.Contains(failure + "original failure (HRESULT 0x80131509)", text);
            Assert.Contains(failure + "edited failure (HRESULT 0x80131509)", text);
        }
        else if (kind == "inputs")
        {
            Assert.Contains($"call 2 argument 0: {scalar} \"5\" -> {scalar} \"0\"", text);
            Assert.Contains($"call 2 argument 0: {scalar} \"6\" -> {scalar} \"0\"", text);
        }
        else
        {
            Assert.Contains($"call 1 argument 0: {scalar} \"5\" -> {scalar} \"6\"", text);
            Assert.Contains($"call 1 argument 0: {scalar} \"5\" -> {scalar} \"7\"", text);
            var receivers = core.Transcript.Lines.Where(line => line.PlainText.Contains("call 1 receiver:", StringComparison.Ordinal))
                .Select(line => line.PlainText.Split(" -> ", StringSplitOptions.None)).ToArray();
            Assert.HasCount(2, receivers);
            Assert.Contains($"{scalar} \"7\"", receivers[0][0]);
            Assert.Contains($"{scalar} \"8\"", receivers[0][1]);
            Assert.Contains($"{scalar} \"7\"", receivers[1][0]);
            Assert.Contains($"{scalar} \"9\"", receivers[1][1]);
            Assert.Contains($"call 1 return: {scalar} \"14\"", text);
            Assert.Contains($"call 1 return: {scalar} \"16\"", text);
        }
    }
}
