using System.Globalization;
using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser workers compare frozen collections through logical contents and comparer settings.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Independent browser runtimes match reordered frozen contents and detect entry and comparer revisions.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="set">Whether to return a frozen set.</param>
    /// <param name="count">The original entry count.</param>
    /// <param name="comparer">The original framework comparer setting.</param>
    /// <param name="comparerOnly">Whether to change only the comparer.</param>
    [TestMethod]
    [DataRow("chromium", false, 0, "default", false)]
    [DataRow("webkit", false, 0, "default", false)]
    [DataRow("chromium", false, 1, "default", false)]
    [DataRow("webkit", false, 1, "default", false)]
    [DataRow("chromium", false, 8, "default", false)]
    [DataRow("webkit", false, 8, "default", false)]
    [DataRow("chromium", true, 0, "default", false)]
    [DataRow("webkit", true, 0, "default", false)]
    [DataRow("chromium", true, 1, "default", false)]
    [DataRow("webkit", true, 1, "default", false)]
    [DataRow("chromium", true, 8, "default", false)]
    [DataRow("webkit", true, 8, "default", false)]
    [DataRow("chromium", false, 8, "InvariantCultureIgnoreCase", false)]
    [DataRow("webkit", false, 8, "InvariantCultureIgnoreCase", false)]
    [DataRow("chromium", true, 8, "InvariantCultureIgnoreCase", false)]
    [DataRow("webkit", true, 8, "InvariantCultureIgnoreCase", false)]
    [DataRow("chromium", false, 0, "Ordinal", true)]
    [DataRow("webkit", false, 0, "Ordinal", true)]
    [DataRow("chromium", false, 8, "Ordinal", true)]
    [DataRow("webkit", false, 8, "Ordinal", true)]
    [DataRow("chromium", true, 0, "Ordinal", true)]
    [DataRow("webkit", true, 0, "Ordinal", true)]
    [DataRow("chromium", true, 8, "Ordinal", true)]
    [DataRow("webkit", true, 8, "Ordinal", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_FrozenCollectionComparisonPreservesContents(string browser, bool set, int count,
        string comparer, bool comparerOnly)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        await SubmitEditSourceAsync(page, FrozenCollectionComparisonExamples.Method(set, count, comparer)
            + "\n.edit Read as Copy {\n" + FrozenCollectionComparisonExamples.Method(set, count, comparer, reverse: true)
            + "\n}", "edit Copy committed as revision 1");
        foreach (var method in new[] { "Read", "Copy" })
            await RunCorpusCellAsync(page, "call " + method + "\ncallvirt instance int32 "
                + FrozenCollectionComparisonExamples.Type(set) + "::get_Count()\nret", count);
        await TypeLineAsync(page, ".compare Copy ()");
        AssertFrozenSide(await WaitForComparisonResultAsync(parent, 0), set, count, false, comparer);
        AssertFrozenSide(await WaitForComparisonResultAsync(parent, 1), set, count, false, comparer);
        await InputIdleAsync(page);
        Assert.Contains("Copy: match", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await InputIdleAsync(page);

        var changedComparer = comparerOnly ? "OrdinalIgnoreCase" : comparer;
        await SubmitEditSourceAsync(page, ".edit Copy {\n"
            + FrozenCollectionComparisonExamples.Method(set, count, changedComparer, edited: !comparerOnly) + "\n}",
            "edit Copy committed as revision 2");
        if (!comparerOnly)
        {
            var entry = FrozenCollectionComparisonExamples.Contents(set, count, edited: true).Last();
            await RunCorpusCellAsync(page, "call Copy\nldstr \"" + entry.Key + "\"\ncallvirt instance "
                + (set ? "bool " + FrozenCollectionComparisonExamples.Type(true) : "int32 class IDictionary<string, int32>")
                + (set ? "::Contains(string)\nconv.i4\nret" : "::get_Item(string)\nret"), set ? 1 : entry.Value);
        }
        await TypeLineAsync(page, ".compare Copy ()");
        AssertFrozenSide(await WaitForComparisonResultAsync(parent, 2), set, count, false, comparer);
        AssertFrozenSide(await WaitForComparisonResultAsync(parent, 3), set, count, !comparerOnly, changedComparer);
        await InputIdleAsync(page);
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertFrozenSide(JsonElement side, bool set, int count, bool edited, string comparer)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var result), json);
        AssertFrozenValue(result, set, count, edited, comparer);
        var invocations = side.GetProperty("invocations");
        Assert.AreEqual(1, invocations.GetArrayLength(), json);
        var invocation = invocations[0];
        Assert.IsFalse(invocation.TryGetProperty("exception", out exception) && exception.ValueKind != JsonValueKind.Null, json);
        var returned = invocation.GetProperty("outputs").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "return").GetProperty("value");
        AssertFrozenValue(returned, set, count, edited, comparer);
    }

    private static void AssertFrozenValue(JsonElement value, bool set, int count, bool edited, string comparer)
    {
        var json = value.GetRawText();
        Assert.AreEqual(JsonValueKind.Object, value.ValueKind, json);
        Assert.AreEqual(set ? "set" : "dictionary", value.GetProperty("kind").GetString(), json);
        const string prefix = "[System.Collections.Immutable]System.Collections.Frozen.";
        const string text = "[System.Private.CoreLib]System.String";
        Assert.AreEqual(prefix + (set ? "FrozenSet`1<" + text + ">"
            : "FrozenDictionary`2<" + text + ",[System.Private.CoreLib]System.Int32>"), value.GetProperty("type").GetString(), json);
        var expected = FrozenCollectionComparisonExamples.Contents(set, count, edited);
        var members = value.GetProperty("members");
        Assert.AreEqual(expected.Count + 1, members.GetArrayLength(), json);
        Assert.AreEqual("comparer", members[0].GetProperty("name").GetString(), json);
        var settings = members[0].GetProperty("value");
        Assert.AreEqual("comparer", settings.GetProperty("kind").GetString(), json);
        Assert.AreEqual(comparer switch { "OrdinalIgnoreCase" => "ordinal-ignore-case", "InvariantCultureIgnoreCase" => ":1",
            _ => "ordinal" }, settings.GetProperty("value").GetString(), json);
        if (set)
        {
            var elements = members.EnumerateArray().Skip(1).Select(member => member.GetProperty("value").GetProperty("value").GetString());
            Assert.AreSequenceEqual(expected.Keys.Order(StringComparer.Ordinal), elements.Order(StringComparer.Ordinal), json);
        }
        else
        {
            var entries = members.EnumerateArray().Skip(1).Select(member => member.GetProperty("value")).ToArray();
            foreach (var entry in entries)
            {
                Assert.AreEqual("entry", entry.GetProperty("kind").GetString(), json);
                Assert.AreEqual(2, entry.GetProperty("members").GetArrayLength(), json);
            }
            var actual = entries.Select(entry => entry.GetProperty("members")).ToDictionary(
                pair => pair[0].GetProperty("value").GetProperty("value").GetString()!,
                pair => pair[1].GetProperty("value").GetProperty("value").GetString());
            foreach (var pair in expected) Assert.AreEqual(pair.Value.ToString(CultureInfo.InvariantCulture), actual[pair.Key], json);
        }
    }
}
