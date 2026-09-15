using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons transport and display stored exception details without invoking user formatting.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Stored parameter names, actual values, and custom data distinguish otherwise identical failures.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The stored exception detail.</param>
    /// <param name="field">The field containing the detail.</param>
    [TestMethod]
    [DataRow("chromium", "parameter", "_paramName")]
    [DataRow("webkit", "parameter", "_paramName")]
    [DataRow("chromium", "actual", "_actualValue")]
    [DataRow("webkit", "actual", "_actualValue")]
    [DataRow("chromium", "custom", "Detail")]
    [DataRow("webkit", "custom", "Detail")]
    [DataRow("chromium", "data", "_data")]
    [DataRow("webkit", "data", "_data")]
    [DataRow("chromium", "help", "_helpURL")]
    [DataRow("webkit", "help", "_helpURL")]
    [DataRow("chromium", "source", "_source")]
    [DataRow("webkit", "source", "_source")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonObservesStoredExceptionFields(string browser, string kind, string field)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ExceptionStateExamples.Source(kind, false) + "\n.edit Read as Copy {\n"
            + ExceptionStateExamples.Method(kind, true) + "\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            var exception = side.GetProperty("invocations")[0].GetProperty("exception");
            Assert.AreEqual("bad", exception.GetProperty("message").GetString());
            var value = exception.GetProperty("fields").EnumerateArray()
                .Single(member => member.GetProperty("name").GetString()!.EndsWith("::" + field, StringComparison.Ordinal))
                .GetProperty("value");
            var expected = kind == "actual" ? index == 0 ? "42" : "43" : index == 0 ? "left" : "right";
            Assert.Contains(expected, ExceptionScalars(value));
        }

        await ExpectComparisonTextAsync(page, "::" + field + " =");
        await InputIdleAsync(page);
        var transcript = await ReadComparisonTranscriptAsync(page);
        Assert.Contains("Copy: different", transcript);
        Assert.Contains("::" + field + " =", transcript);
        Assert.Contains(kind == "actual" ? "\"42\"" : "\"left\"", transcript);
        Assert.Contains(kind == "actual" ? "\"43\"" : "\"right\"", transcript);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static IEnumerable<string?> ExceptionScalars(JsonElement value)
    {
        if (value.GetProperty("kind").GetString() == "scalar") yield return value.GetProperty("value").GetString();
        foreach (var member in value.GetProperty("members").EnumerateArray())
        {
            foreach (var scalar in ExceptionScalars(member.GetProperty("value"))) yield return scalar;
        }
    }
}
