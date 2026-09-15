using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser workers compare LINQ lookups by their logical groups and comparer.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Equal lookup groups match in separate workers while an edited group value remains visible.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonObservesLookupContents(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var source = LookupComparisonExamples.Method(edited: false);
        await SubmitEditSourceAsync(page, LookupComparisonExamples.KeyMethod + "\n" + source
            + "\n.edit Read as Copy {\n" + source + "\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        for (var index = 0; index < 2; index++)
            AssertLookupSide(await WaitForComparisonResultAsync(parent, index), "43");
        await ExpectComparisonTextAsync(page, "edited: completed");
        await InputIdleAsync(page);
        Assert.Contains("Copy: match", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await ExpectComparisonTextAsync(page, "edited: completed");
        await InputIdleAsync(page);

        await SubmitEditSourceAsync(page, ".edit Copy {\n" + LookupComparisonExamples.Method(edited: true) + "\n}",
            "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        AssertLookupSide(await WaitForComparisonResultAsync(parent, 2), "43");
        AssertLookupSide(await WaitForComparisonResultAsync(parent, 3), "45");
        await ExpectComparisonTextAsync(page, "edited: completed");
        await InputIdleAsync(page);
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertLookupSide(JsonElement side, string second)
    {
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
        var value = side.GetProperty("result");
        Assert.AreEqual("lookup", value.GetProperty("kind").GetString());
        var members = value.GetProperty("members");
        Assert.AreEqual("first", members[1].GetProperty("value").GetProperty("members")[0]
            .GetProperty("value").GetProperty("value").GetString());
        Assert.AreEqual(second, members[2].GetProperty("value").GetProperty("members")[1]
            .GetProperty("value").GetProperty("value").GetString());
    }
}
