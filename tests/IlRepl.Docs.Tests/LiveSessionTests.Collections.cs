using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser workers observe collection contents independently of their runtime hash storage.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Dictionary and set contents match in independent runtimes while edited entries remain distinguishable.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="set">Whether to return a hash set.</param>
    /// <param name="comparer">The framework string comparer.</param>
    [TestMethod]
    [DataRow("chromium", false, "default")]
    [DataRow("webkit", false, "default")]
    [DataRow("chromium", false, "OrdinalIgnoreCase")]
    [DataRow("webkit", false, "OrdinalIgnoreCase")]
    [DataRow("chromium", false, "InvariantCulture")]
    [DataRow("webkit", false, "InvariantCulture")]
    [DataRow("chromium", true, "default")]
    [DataRow("webkit", true, "default")]
    [DataRow("chromium", true, "OrdinalIgnoreCase")]
    [DataRow("webkit", true, "OrdinalIgnoreCase")]
    [DataRow("chromium", true, "InvariantCulture")]
    [DataRow("webkit", true, "InvariantCulture")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonObservesCollectionContents(string browser, bool set, string comparer)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var source = CollectionComparisonExamples.Method(set, comparer, false);
        await SubmitEditSourceAsync(page, source + "\n.edit Read as Copy {\n" + source + "\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            var value = side.GetProperty("result");
            Assert.AreEqual(set ? "set" : "dictionary", value.GetProperty("kind").GetString());
            var members = value.GetProperty("members");
            Assert.AreEqual(3, members.GetArrayLength());
            Assert.AreEqual("comparer", members[0].GetProperty("name").GetString());
            var first = members[1].GetProperty("value");
            Assert.AreEqual("first", set ? first.GetProperty("value").GetString()
                : first.GetProperty("members")[0].GetProperty("value").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "edited: completed");
        await InputIdleAsync(page);
        Assert.Contains("Copy: match", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await ExpectComparisonTextAsync(page, "edited: completed");
        await InputIdleAsync(page);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + CollectionComparisonExamples.Method(set, comparer, true) + "\n}",
            "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var (index, expected) in new[] { (2, set ? "second" : "43"), (3, set ? "changed" : "44") })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            var item = side.GetProperty("result").GetProperty("members")[2].GetProperty("value");
            Assert.AreEqual(expected, set ? item.GetProperty("value").GetString()
                : item.GetProperty("members")[1].GetProperty("value").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "edited: completed");
        await InputIdleAsync(page);
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
