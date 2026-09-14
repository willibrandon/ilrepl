using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser copies preserve methods bound by name through the standard delegate factory overloads.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Name-based delegate binding survives revisions, comparison workers, and saving and reopening the compiled copy.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="staticTarget">Whether the named target is static.</param>
    /// <param name="options">The number of delegate-binding options supplied.</param>
    [TestMethod]
    [DataRow("chromium", false, 0)]
    [DataRow("webkit", false, 0)]
    [DataRow("chromium", false, 1)]
    [DataRow("webkit", false, 1)]
    [DataRow("chromium", false, 2)]
    [DataRow("webkit", false, 2)]
    [DataRow("chromium", true, 0)]
    [DataRow("webkit", true, 0)]
    [DataRow("chromium", true, 1)]
    [DataRow("webkit", true, 1)]
    [DataRow("chromium", true, 2)]
    [DataRow("webkit", true, 2)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesNameBoundDelegates(string browser, bool staticTarget, int options)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, DelegateReflectionExamples.Source(staticTarget, options) + "\n.edit "
            + DelegateReflectionExamples.Reference(staticTarget) + " as Copy {\n"
            + DelegateReflectionExamples.Method(staticTarget, options, false) + "\n}\n"
            + DelegateReflectionExamples.Scenario(staticTarget), "end of method Scenario");
        await RunCorpusCellAsync(page, "call Scenario\nret", 42);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + DelegateReflectionExamples.Method(staticTarget, options, true) + "\n}",
            "edit Copy committed as revision 2");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        Assert.AreEqual("42", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("43", edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, "edited: completed");
        await ExpectComparisonTextAsync(page, "call 1 return: [System.Private.CoreLib]System.Int32 \"43\"");
        await TypeLineAsync(page, "call Scenario");
        await TypeLineAsync(page, ".save /tmp/named-delegate.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/named-delegate.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/named-delegate.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [named-delegate]IlRepl.Cell::Run()\nret", 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
