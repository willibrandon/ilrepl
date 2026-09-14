using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser reports show real nested exception chains and incomplete exception observations.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Inner messages and cyclic-chain problems stay visible whether the scenario catches the method exception or lets it escape.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="caught">Whether the scenario catches the exception.</param>
    /// <param name="cycle">Whether the revision creates a cyclic exception chain.</param>
    [TestMethod]
    [DataRow("chromium", false, false)]
    [DataRow("webkit", false, false)]
    [DataRow("chromium", true, false)]
    [DataRow("webkit", true, false)]
    [DataRow("chromium", false, true)]
    [DataRow("webkit", false, true)]
    [DataRow("chromium", true, true)]
    [DataRow("webkit", true, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonShowsNestedExceptionsAndProblems(string browser, bool caught, bool cycle)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ExceptionChainExamples.Method(cycle, false) + "\n.edit Read as Copy {\n"
            + ExceptionChainExamples.Method(cycle, true) + "\n}\n" + ExceptionChainExamples.Scenario(caught), "end of method Scenario");
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "edited: completed");
        var text = await ReadComparisonTranscriptAsync(page);
        Assert.Contains("Copy: " + (cycle ? "incomplete" : "different"), text);
        Assert.Contains("call 1 threw [System.Private.CoreLib]System.Exception: outer failure", text);
        Assert.Contains("caused by [System.Private.CoreLib]System.InvalidOperationException: middle failure", text);
        Assert.Contains("caused by [System.Private.CoreLib]System.ArgumentException: original detail", text);
        Assert.Contains("caused by [System.Private.CoreLib]System.ArgumentException: edited detail", text);
        if (cycle)
        {
            Assert.Contains("unavailable: exception chain is cyclic or exceeds the observation depth limit", text);
        }

        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
