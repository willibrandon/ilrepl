using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser workers retain the difference between a null task and a completed task with no result.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Direct comparisons preserve task presence separately from the awaited payload.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The non-generic, integer, or nullable string task shape.</param>
    /// <param name="originalPresent">Whether the original returns a task object.</param>
    /// <param name="editedPresent">Whether the edit returns a task object.</param>
    [TestMethod]
    [DataRow("chromium", 0, false, true)]
    [DataRow("webkit", 0, false, true)]
    [DataRow("chromium", 0, true, false)]
    [DataRow("webkit", 0, true, false)]
    [DataRow("chromium", 0, false, false)]
    [DataRow("webkit", 0, false, false)]
    [DataRow("chromium", 0, true, true)]
    [DataRow("webkit", 0, true, true)]
    [DataRow("chromium", 1, false, true)]
    [DataRow("webkit", 1, false, true)]
    [DataRow("chromium", 2, false, true)]
    [DataRow("webkit", 2, false, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_TaskPresenceComparisonDistinguishesNull(string browser, int kind,
        bool originalPresent, bool editedPresent)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        await SubmitEditSourceAsync(page, TaskPresenceComparisonExamples.Method(kind, originalPresent)
            + "\n.edit Read as Copy {\n" + TaskPresenceComparisonExamples.Method(kind, editedPresent) + "\n}",
            "edit Copy committed as revision 1");
        foreach (var (method, present) in new[] { ("Read", originalPresent), ("Copy", editedPresent) })
        {
            await RunCorpusCellAsync(page, "call " + method + "\nldnull\nceq\nret", present ? 0 : 1);
            if (present)
            {
                await RunCorpusCellAsync(page, "call " + method
                    + "\ncallvirt instance bool System.Threading.Tasks.Task::get_IsCompletedSuccessfully()\nconv.i4\nret", 1);
            }
        }

        await TypeLineAsync(page, ".compare Copy ()");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        AssertTaskPresenceSide(original, kind, originalPresent);
        AssertTaskPresenceSide(edited, kind, editedPresent);
        AssertTaskPresenceValue(original.GetProperty("result"), kind, originalPresent);
        AssertTaskPresenceValue(edited.GetProperty("result"), kind, editedPresent);
        await ExpectComparisonTextAsync(page, "edited: completed");
        var report = await ReadComparisonTranscriptAsync(page);
        Assert.Contains("Copy: " + (originalPresent == editedPresent ? "match" : "different"), report);
        if (!originalPresent || !editedPresent) Assert.Contains("null Task", report);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// Selected invocation reports distinguish null even when the scenario discards its return value.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="originalPresent">Whether the original returns a task object.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_TaskPresenceComparisonPreservesScenarioBranches(string browser, bool originalPresent)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        await SubmitEditSourceAsync(page, TaskPresenceComparisonExamples.Method(0, originalPresent)
            + "\n.edit Read as Copy {\n" + TaskPresenceComparisonExamples.Method(0, !originalPresent) + "\n}\n"
            + TaskPresenceComparisonExamples.IgnoredScenario(), "end of method Scenario");

        await TypeLineAsync(page, ".compare Copy using Scenario");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        AssertTaskPresenceSide(original, 0, originalPresent);
        AssertTaskPresenceSide(edited, 0, !originalPresent);
        foreach (var side in new[] { original, edited })
        {
            var result = side.GetProperty("result");
            Assert.AreEqual("scalar", result.GetProperty("kind").GetString(), side.GetRawText());
            Assert.AreEqual("42", result.GetProperty("value").GetString(), side.GetRawText());
        }
        await ExpectComparisonTextAsync(page, "edited: completed");
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));

        await SubmitEditSourceAsync(page, TaskPresenceComparisonExamples.BranchScenario(), "end of method Scenario");
        await RunCorpusCellAsync(page, "call Scenario\nret", originalPresent ? 41 : 42);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        original = await WaitForComparisonResultAsync(parent, 2);
        edited = await WaitForComparisonResultAsync(parent, 3);
        AssertTaskPresenceSide(original, 0, originalPresent);
        AssertTaskPresenceSide(edited, 0, !originalPresent);
        Assert.AreEqual(originalPresent ? "42" : "41", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual(originalPresent ? "41" : "42", edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, "edited: completed");
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertTaskPresenceSide(JsonElement side, int kind, bool present)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var result), json);
        Assert.AreEqual(JsonValueKind.Object, result.ValueKind, json);
        Assert.AreEqual("", side.GetProperty("standardOutput").GetString(), json);
        Assert.AreEqual("", side.GetProperty("standardError").GetString(), json);
        var invocations = side.GetProperty("invocations");
        Assert.AreEqual(1, invocations.GetArrayLength(), json);
        var invocation = invocations[0];
        Assert.IsFalse(invocation.TryGetProperty("exception", out exception) && exception.ValueKind != JsonValueKind.Null, json);
        var inputs = invocation.GetProperty("inputs");
        Assert.AreEqual(1, inputs.GetArrayLength(), json);
        Assert.AreEqual("receiver", inputs[0].GetProperty("name").GetString(), json);
        Assert.AreEqual("null", inputs[0].GetProperty("value").GetProperty("kind").GetString(), json);
        var outputs = invocation.GetProperty("outputs");
        Assert.AreEqual(2, outputs.GetArrayLength(), json);
        var returned = outputs.EnumerateArray().Single(member => member.GetProperty("name").GetString() == "return");
        AssertTaskPresenceValue(returned.GetProperty("value"), kind, present);
    }

    private static void AssertTaskPresenceValue(JsonElement value, int kind, bool present)
    {
        var json = value.GetRawText();
        Assert.AreEqual(!present ? "null-task" : kind == 1 ? "scalar" : "null", value.GetProperty("kind").GetString(), json);
        var type = !present ? "System.Threading.Tasks.Task" : kind == 1 ? "[System.Private.CoreLib]System.Int32" : "";
        Assert.AreEqual(type, value.GetProperty("type").GetString(), json);
        Assert.AreEqual(0, value.GetProperty("members").GetArrayLength(), json);
        if (present && kind == 1)
        {
            Assert.AreEqual("42", value.GetProperty("value").GetString(), json);
        }
        else
        {
            Assert.IsFalse(value.TryGetProperty("value", out var scalar) && scalar.ValueKind != JsonValueKind.Null, json);
            Assert.IsFalse(value.TryGetProperty("identity", out var identity) && identity.ValueKind != JsonValueKind.Null, json);
        }
    }
}
