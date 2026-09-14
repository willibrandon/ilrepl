using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Exception observations remain serializable through the browser worker boundary at the capture depth limit.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A deep exception chain reaches the parent with its original messages and an explicit observation problem.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonTransportsDeepExceptionChains(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ExceptionChainExamples.Method(false, false) + "\n.edit Read as Copy {\n"
            + ExceptionChainExamples.Method(false, true, depth: true) + "\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        var details = await parent.EvaluateAsync<string[]>("""
            async () => {
              while (self.comparisonResults.length < 2) await new Promise(resolve => setTimeout(resolve, 10));
              const [original, edited] = self.comparisonResults;
              const exception = edited.invocations[0].exception;
              let last = exception;
              while (last.inner) last = last.inner;
              return [original.outcome, edited.outcome, exception.message, exception.inner.message,
                exception.inner.inner.message, last.problem];
            }
            """).WaitAsync(TestContext.CancellationToken);
        Assert.AreSequenceEqual(["completed", "completed", "outer failure", "middle failure", "edited detail",
            "exception chain is cyclic or exceeds the observation depth limit"], details);
        await InputIdleAsync(page);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
