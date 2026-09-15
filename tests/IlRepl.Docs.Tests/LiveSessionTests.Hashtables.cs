using System.Globalization;
using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser workers compare hashtable entries and comparer settings without randomized storage.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Plain, synchronized, nested and legacy-comparer tables match unchanged and distinguish real revisions.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="count">The original number of entries.</param>
    /// <param name="wrappers">The synchronized wrapper count.</param>
    /// <param name="comparer">The original comparer setting.</param>
    /// <param name="comparerOnly">Whether to change only the comparer.</param>
    [TestMethod]
    [DataRow("chromium", 0, 0, "default", false)]
    [DataRow("webkit", 0, 0, "default", false)]
    [DataRow("chromium", 1, 0, "default", false)]
    [DataRow("webkit", 1, 0, "default", false)]
    [DataRow("chromium", 8, 0, "default", false)]
    [DataRow("webkit", 8, 0, "default", false)]
    [DataRow("chromium", 8, 1, "default", false)]
    [DataRow("webkit", 8, 1, "default", false)]
    [DataRow("chromium", 8, 2, "OrdinalIgnoreCase", false)]
    [DataRow("webkit", 8, 2, "OrdinalIgnoreCase", false)]
    [DataRow("chromium", 8, 1, "legacy", false)]
    [DataRow("webkit", 8, 1, "legacy", false)]
    [DataRow("chromium", 0, 0, "Ordinal", true)]
    [DataRow("webkit", 0, 0, "Ordinal", true)]
    [DataRow("chromium", 8, 1, "Ordinal", true)]
    [DataRow("webkit", 8, 1, "Ordinal", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_HashtableComparisonPreservesLogicalContents(string browser, int count, int wrappers,
        string comparer, bool comparerOnly)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        await SubmitEditSourceAsync(page, HashtableComparisonExamples.Method(count, wrappers, comparer)
            + "\n.edit Read as Copy {\n" + HashtableComparisonExamples.Method(count, wrappers, comparer, reverse: true)
            + "\n}", "edit Copy committed as revision 1");
        foreach (var method in new[] { "Read", "Copy" })
        {
            await RunCorpusCellAsync(page, "call " + method + "\ncallvirt instance int32 Hashtable::get_Count()\nret", count);
            await RunCorpusCellAsync(page, "call " + method
                + "\ncallvirt instance bool Hashtable::get_IsSynchronized()\nconv.i4\nret", wrappers > 0 ? 1 : 0);
        }
        await TypeLineAsync(page, ".compare Copy ()");
        AssertHashtableSide(await WaitForComparisonResultAsync(parent, 0), count, false, wrappers, comparer);
        AssertHashtableSide(await WaitForComparisonResultAsync(parent, 1), count, false, wrappers, comparer);
        await InputIdleAsync(page);
        Assert.Contains("Copy: match", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await InputIdleAsync(page);

        var changedComparer = comparerOnly ? "OrdinalIgnoreCase" : comparer;
        var changed = HashtableComparisonExamples.Method(count, wrappers, changedComparer, edited: !comparerOnly);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + changed + "\n}", "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        AssertHashtableSide(await WaitForComparisonResultAsync(parent, 2), count, false, wrappers, comparer);
        AssertHashtableSide(await WaitForComparisonResultAsync(parent, 3), count, !comparerOnly, wrappers, changedComparer);
        await InputIdleAsync(page);
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertHashtableSide(JsonElement side, int count, bool edited, int wrappers, string comparer)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var result), json);
        AssertHashtableValue(result, count, edited, wrappers, comparer);
        var invocations = side.GetProperty("invocations");
        Assert.AreEqual(1, invocations.GetArrayLength(), json);
        var invocation = invocations[0];
        Assert.IsFalse(invocation.TryGetProperty("exception", out exception) && exception.ValueKind != JsonValueKind.Null, json);
        var returned = invocation.GetProperty("outputs").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "return").GetProperty("value");
        AssertHashtableValue(returned, count, edited, wrappers, comparer);
    }

    private static void AssertHashtableValue(JsonElement value, int count, bool edited, int wrappers, string comparer)
    {
        var json = value.GetRawText();
        for (var index = 0; index < wrappers; index++)
        {
            Assert.AreEqual("dictionary", value.GetProperty("kind").GetString(), json);
            Assert.EndsWith("System.Collections.Hashtable+SyncHashtable", value.GetProperty("type").GetString()!, json);
            var fields = value.GetProperty("members");
            Assert.AreEqual(1, fields.GetArrayLength(), json);
            Assert.AreEqual("table", fields[0].GetProperty("name").GetString(), json);
            value = fields[0].GetProperty("value");
        }
        Assert.AreEqual("dictionary", value.GetProperty("kind").GetString(), json);
        Assert.EndsWith("System.Collections.Hashtable", value.GetProperty("type").GetString()!, json);
        var expected = HashtableComparisonExamples.Contents(count, edited);
        var members = value.GetProperty("members");
        Assert.AreEqual(expected.Count + 1, members.GetArrayLength(), json);
        Assert.AreEqual("comparer", members[0].GetProperty("name").GetString(), json);
        var settings = members[0].GetProperty("value");
        Assert.AreEqual(comparer == "default" ? "null" : comparer == "legacy" ? "object" : "comparer",
            settings.GetProperty("kind").GetString(), json);
        if (comparer == "legacy")
        {
            Assert.AreEqual(2, settings.GetProperty("members").GetArrayLength(), json);
            foreach (var member in settings.GetProperty("members").EnumerateArray())
            {
                Assert.AreEqual("comparer", member.GetProperty("value").GetProperty("kind").GetString(), json);
                Assert.AreEqual(":1", member.GetProperty("value").GetProperty("value").GetString(), json);
            }
        }
        else if (comparer != "default")
            Assert.AreEqual(comparer == "Ordinal" ? "ordinal" : "ordinal-ignore-case",
                settings.GetProperty("value").GetString(), json);
        var actual = members.EnumerateArray().Skip(1).Select(member => member.GetProperty("value").GetProperty("members"))
            .ToDictionary(pair => pair[0].GetProperty("value").GetProperty("value").GetString()!,
                pair => pair[1].GetProperty("value").GetProperty("value").GetString());
        Assert.HasCount(expected.Count, actual, json);
        foreach (var pair in expected) Assert.AreEqual(pair.Value.ToString(CultureInfo.InvariantCulture), actual[pair.Key], json);
    }
}
