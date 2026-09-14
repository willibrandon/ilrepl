using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Generated comparison modules expose the same identity in real browser workers.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Module identity alone cannot create a difference, while an explicit edited Guid.Empty remains distinguishable.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the declaring owner has a type parameter.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonSharesGeneratedModuleIdentity(string browser, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ModuleIdentityExamples.Source(generic) + "\n.edit "
            + ModuleIdentityExamples.Reference(generic) + " as Copy {\n" + ModuleIdentityExamples.Method(generic, false) + "\n}",
            "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        var identity = original.GetProperty("result").GetProperty("value").GetString();
        var empty = Convert.ToHexString(Guid.Empty.ToByteArray());
        Assert.IsNotNull(identity);
        Assert.AreNotEqual(empty, identity);
        Assert.AreEqual(identity, edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, "edited: completed");
        await InputIdleAsync(page);
        Assert.Contains("Copy: match", await ReadComparisonTranscriptAsync(page));
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + ModuleIdentityExamples.Method(generic, true) + "\n}",
            "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        original = await WaitForComparisonResultAsync(parent, 2);
        edited = await WaitForComparisonResultAsync(parent, 3);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        Assert.AreNotEqual(empty, original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual(empty, edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, empty);
        await InputIdleAsync(page);
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
