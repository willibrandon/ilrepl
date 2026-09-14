using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons preserve aggregate children across worker transport and display every observed failure.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Later aggregate children remain visible and change the comparison outcome in both browser runtimes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="nested">Whether the differing child belongs to a nested aggregate.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonObservesAllAggregateChildren(string browser, bool nested)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, AggregateExceptionExamples.Method(false, nested) + "\n.edit Read as Copy {\n"
            + AggregateExceptionExamples.Method(true, nested) + "\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var (index, expected) in new[] { (0, "original detail"), (1, "edited detail") })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            var exception = side.GetProperty("invocations")[0].GetProperty("exception");
            Assert.AreEqual("outer failure", exception.GetProperty("message").GetString());
            Assert.AreEqual("first failure", exception.GetProperty("inner").GetProperty("message").GetString());
            var later = exception.GetProperty("additionalInnerExceptions")[0];
            if (nested) later = later.GetProperty("additionalInnerExceptions")[0];
            Assert.AreEqual(expected, later.GetProperty("message").GetString());
        }

        await ExpectComparisonTextAsync(page, "System.ArgumentException: edited detail");
        await InputIdleAsync(page);
        var transcript = await ReadComparisonTranscriptAsync(page);
        Assert.Contains("Copy: different", transcript);
        Assert.Contains("System.ArgumentException: original detail", transcript);
        Assert.Contains("System.ArgumentException: edited detail", transcript);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// A later child's truncated chain survives worker serialization and produces an incomplete comparison.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonTransportsDeepAggregateChildren(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var source = AggregateExceptionExamples.DeepMethod();
        await SubmitEditSourceAsync(page, source + "\n.edit Read as Copy {\n" + source + "\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        var details = await parent.EvaluateAsync<string[]>("""
            async () => {
              while (self.comparisonResults.length < 2) await new Promise(resolve => setTimeout(resolve, 10));
              return self.comparisonResults.flatMap(side => {
                const exception = side.invocations[0].exception;
                let last = exception.additionalInnerExceptions[0];
                while (last.inner) last = last.inner;
                return [side.outcome, exception.message, exception.inner.message, last.problem];
              });
            }
            """).WaitAsync(TestContext.CancellationToken);
        Assert.AreSequenceEqual(["completed", "outer failure", "first failure",
            "exception chain is cyclic or exceeds the observation depth limit",
            "completed", "outer failure", "first failure",
            "exception chain is cyclic or exceeds the observation depth limit"], details);

        await ExpectComparisonTextAsync(page, "exception chain is cyclic");
        await InputIdleAsync(page);
        Assert.Contains("Copy: incomplete", await ReadComparisonTranscriptAsync(page, maximumSnapshots: 256));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
