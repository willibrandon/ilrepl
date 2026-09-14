using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Real browser workers observe immutable hash collections independently of their randomized storage order.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Immutable contents and both dictionary comparers match unchanged and distinguish edits in independent browser runtimes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="set">Whether to return an immutable set.</param>
    /// <param name="count">The original number of entries.</param>
    /// <param name="change">Whether to change contents, the key comparer, or the value comparer.</param>
    /// <returns>The completed unchanged and revised worker assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false, 0, "contents")]
    [DataRow("webkit", false, 0, "contents")]
    [DataRow("chromium", false, 1, "contents")]
    [DataRow("webkit", false, 1, "contents")]
    [DataRow("chromium", false, 8, "contents")]
    [DataRow("webkit", false, 8, "contents")]
    [DataRow("chromium", true, 0, "contents")]
    [DataRow("webkit", true, 0, "contents")]
    [DataRow("chromium", true, 1, "contents")]
    [DataRow("webkit", true, 1, "contents")]
    [DataRow("chromium", true, 8, "contents")]
    [DataRow("webkit", true, 8, "contents")]
    [DataRow("chromium", false, 0, "key")]
    [DataRow("webkit", false, 0, "key")]
    [DataRow("chromium", false, 0, "value")]
    [DataRow("webkit", false, 0, "value")]
    [DataRow("chromium", false, 8, "key")]
    [DataRow("webkit", false, 8, "key")]
    [DataRow("chromium", false, 8, "value")]
    [DataRow("webkit", false, 8, "value")]
    [DataRow("chromium", true, 8, "key")]
    [DataRow("webkit", true, 8, "key")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonObservesImmutableContents(string browser, bool set, int count, string change)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var source = ImmutableCollectionComparisonExamples.Method(set, count);
        await SubmitEditSourceAsync(page, source + "\n.edit Read as Copy {\n" + source + "\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            AssertImmutableCollectionSide(side, set, count, false);
        }
        await InputIdleAsync(page);
        Assert.Contains("Copy: match", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await InputIdleAsync(page);
        var changed = ImmutableCollectionComparisonExamples.Method(set, count,
            change == "key" ? "OrdinalIgnoreCase" : "default", change == "value" ? "OrdinalIgnoreCase" : "default",
            edited: change == "contents");
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + changed + "\n}", "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 2, 3 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            AssertImmutableCollectionSide(side, set, count, index == 3 && change == "contents");
            if (change != "contents")
            {
                var value = side.GetProperty("result");
                var comparer = ImmutableComparer(value, change == "key" ? "comparer" : "value comparer");
                Assert.AreEqual(index == 3 ? "ordinal-ignore-case" : "ordinal", comparer.GetProperty("value").GetString(),
                    side.GetRawText());
            }
        }
        await InputIdleAsync(page);
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertImmutableCollectionSide(JsonElement side, bool set, int count, bool edited)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var value), json);
        Assert.AreEqual(JsonValueKind.Object, value.ValueKind, json);
        Assert.AreEqual(set ? "set" : "dictionary", value.GetProperty("kind").GetString(), json);
        Assert.AreEqual(1, side.GetProperty("invocations").GetArrayLength(), json);
        var expected = ImmutableCollectionComparisonExamples.Contents(count, edited, set);
        var members = value.GetProperty("members").EnumerateArray().ToArray();
        Assert.HasCount(expected.Count + (set ? 1 : 2), members, json);
        Assert.AreEqual("comparer", ImmutableComparer(value, "comparer").GetProperty("kind").GetString(), json);
        if (set)
        {
            var actual = members.Where(member => member.GetProperty("name").GetString() != "comparer")
                .Select(member => member.GetProperty("value").GetProperty("value").GetString()!);
            Assert.AreSequenceEqual(expected.Keys.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal), json);
        }
        else
        {
            Assert.AreEqual("comparer", ImmutableComparer(value, "value comparer").GetProperty("kind").GetString(), json);
            var actual = members.Select(member => member.GetProperty("value"))
                .Where(item => item.GetProperty("kind").GetString() == "entry")
                .Select(item => item.GetProperty("members"))
                .ToDictionary(item => item[0].GetProperty("value").GetProperty("value").GetString()!,
                    item => item[1].GetProperty("value").GetProperty("value").GetString());
            Assert.HasCount(expected.Count, actual, json);
            foreach (var (key, item) in expected) Assert.AreEqual(item, actual[key], json);
        }
    }

    private static JsonElement ImmutableComparer(JsonElement collection, string name)
    {
        var members = collection.GetProperty("members").EnumerateArray().ToArray();
        var comparer = members.Single(member => member.GetProperty("name").GetString() == name).GetProperty("value");
        if (comparer.GetProperty("kind").GetString() != "reference") return comparer;
        var identity = comparer.GetProperty("identity").GetInt32();
        return members.Select(member => member.GetProperty("value")).Single(value => value.GetProperty("kind").GetString() != "reference"
            && value.TryGetProperty("identity", out var candidate) && candidate.GetInt32() == identity);
    }
}
