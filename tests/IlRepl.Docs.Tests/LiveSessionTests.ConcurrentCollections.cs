using System.Globalization;
using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser workers compare concurrent dictionaries by logical contents and comparer settings.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Independent runtimes match reordered dictionaries and distinguish revised contents or comparers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="count">The original entry count.</param>
    /// <param name="comparerOnly">Whether to change only the comparer.</param>
    [TestMethod]
    [DataRow("chromium", 0, false)]
    [DataRow("webkit", 0, false)]
    [DataRow("chromium", 1, false)]
    [DataRow("webkit", 1, false)]
    [DataRow("chromium", 8, false)]
    [DataRow("webkit", 8, false)]
    [DataRow("chromium", 0, true)]
    [DataRow("webkit", 0, true)]
    [DataRow("chromium", 8, true)]
    [DataRow("webkit", 8, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ConcurrentCollectionComparisonPreservesContents(string browser, int count, bool comparerOnly)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        await SubmitEditSourceAsync(page, ConcurrentCollectionComparisonExamples.Method(count) + "\n.edit Read as Copy {\n"
            + ConcurrentCollectionComparisonExamples.Method(count, reverse: true) + "\n}", "edit Copy committed as revision 1");
        foreach (var method in new[] { "Read", "Copy" })
            await RunCorpusCellAsync(page, "call " + method
                + "\ncallvirt instance int32 class ConcurrentDictionary<string, int32>::get_Count()\nret", count);

        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
            AssertConcurrentCollectionSide(await WaitForComparisonResultAsync(parent, index), count, false, "ordinal");
        await InputIdleAsync(page);
        Assert.Contains("Copy: match", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await InputIdleAsync(page);

        var source = ConcurrentCollectionComparisonExamples.Method(count, comparerOnly ? "OrdinalIgnoreCase" : "default",
            edited: !comparerOnly, reverse: true);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + source + "\n}", "edit Copy committed as revision 2");
        if (!comparerOnly)
        {
            var entry = ConcurrentCollectionComparisonExamples.Contents(count, edited: true).Last();
            await RunCorpusCellAsync(page, "call Copy\nldstr \"" + entry.Key
                + "\"\ncallvirt instance int32 class ConcurrentDictionary<string, int32>::get_Item(string)\nret", entry.Value);
        }
        await TypeLineAsync(page, ".compare Copy ()");
        AssertConcurrentCollectionSide(await WaitForComparisonResultAsync(parent, 2), count, false, "ordinal");
        AssertConcurrentCollectionSide(await WaitForComparisonResultAsync(parent, 3), count, !comparerOnly,
            comparerOnly ? "ordinal-ignore-case" : "ordinal");
        await InputIdleAsync(page);
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertConcurrentCollectionSide(JsonElement side, int count, bool edited, string comparer)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var value), json);
        Assert.AreEqual(JsonValueKind.Object, value.ValueKind, json);
        AssertConcurrentCollectionValue(value, count, edited, comparer);
        var invocations = side.GetProperty("invocations");
        Assert.AreEqual(1, invocations.GetArrayLength(), json);
        var invocation = invocations[0];
        Assert.IsFalse(invocation.TryGetProperty("exception", out exception) && exception.ValueKind != JsonValueKind.Null, json);
        var returned = invocation.GetProperty("outputs").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "return").GetProperty("value");
        AssertConcurrentCollectionValue(returned, count, edited, comparer);
    }

    private static void AssertConcurrentCollectionValue(JsonElement value, int count, bool edited, string comparer)
    {
        var json = value.GetRawText();
        Assert.AreEqual("dictionary", value.GetProperty("kind").GetString(), json);
        Assert.Contains("System.Collections.Concurrent.ConcurrentDictionary`2", value.GetProperty("type").GetString()!, json);
        var expected = ConcurrentCollectionComparisonExamples.Contents(count, edited);
        var members = value.GetProperty("members");
        Assert.AreEqual(expected.Count + 1, members.GetArrayLength(), json);
        Assert.AreEqual("comparer", members[0].GetProperty("name").GetString(), json);
        var observedComparer = members[0].GetProperty("value");
        Assert.AreEqual("comparer", observedComparer.GetProperty("kind").GetString(), json);
        Assert.AreEqual(comparer, observedComparer.GetProperty("value").GetString(), json);
        var entries = members.EnumerateArray().Skip(1).Select(member => member.GetProperty("value")).ToArray();
        foreach (var entry in entries)
        {
            Assert.AreEqual("entry", entry.GetProperty("kind").GetString(), json);
            Assert.AreEqual(2, entry.GetProperty("members").GetArrayLength(), json);
        }
        var actual = entries.Select(entry => entry.GetProperty("members")).ToDictionary(
            entry => entry[0].GetProperty("value").GetProperty("value").GetString()!,
            entry => entry[1].GetProperty("value").GetProperty("value").GetString());
        Assert.HasCount(expected.Count, actual, json);
        foreach (var entry in expected) Assert.AreEqual(entry.Value.ToString(CultureInfo.InvariantCulture), actual[entry.Key], json);
    }
}
