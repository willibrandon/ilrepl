using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser observation helpers retain receiver identity and the semantics of each invocation instruction.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Direct, virtual, constrained, and delegate calls preserve receiver mutation and null checks in real browser workers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The selected invocation operation.</param>
    /// <param name="valueType">Whether the receiver is a struct.</param>
    /// <param name="generic">Whether the owner is generic.</param>
    [TestMethod]
    [DataRow("chromium", "call", false, false)]
    [DataRow("webkit", "call", false, false)]
    [DataRow("chromium", "call", true, false)]
    [DataRow("webkit", "call", true, false)]
    [DataRow("chromium", "virtual", false, false)]
    [DataRow("webkit", "virtual", false, false)]
    [DataRow("chromium", "virtual", false, true)]
    [DataRow("webkit", "virtual", false, true)]
    [DataRow("chromium", "constrained", false, false)]
    [DataRow("webkit", "constrained", false, false)]
    [DataRow("chromium", "constrained", true, false)]
    [DataRow("webkit", "constrained", true, false)]
    [DataRow("chromium", "constrained", false, true)]
    [DataRow("webkit", "constrained", false, true)]
    [DataRow("chromium", "constrained", true, true)]
    [DataRow("webkit", "constrained", true, true)]
    [DataRow("chromium", "delegate", false, false)]
    [DataRow("webkit", "delegate", false, false)]
    [DataRow("chromium", "delegate", false, true)]
    [DataRow("webkit", "delegate", false, true)]
    [DataRow("chromium", "null-call", false, false)]
    [DataRow("webkit", "null-call", false, false)]
    [DataRow("chromium", "null-virtual", false, false)]
    [DataRow("webkit", "null-virtual", false, false)]
    [DataRow("chromium", "null-delegate", false, false)]
    [DataRow("webkit", "null-delegate", false, false)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesReceiverCallSemantics(string browser, string kind, bool valueType, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ComparisonReceiverCallExamples.Source(valueType, generic) + "\n.edit "
            + ComparisonReceiverCallExamples.Reference(generic) + " as Copy {\n" + ComparisonReceiverCallExamples.Method(generic, true)
            + "\n}\n" + ComparisonReceiverCallExamples.Scenario(kind, valueType, generic), "end of method Scenario");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        foreach (var (side, expected) in new[] { (original, "8"), (edited, "9") })
        {
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.IsFalse(side.TryGetProperty("exception", out _), side.GetRawText());
            Assert.AreEqual(expected, side.GetProperty("result").GetProperty("value").GetString());
            var invocations = side.GetProperty("invocations");
            Assert.AreEqual(kind == "null-call" ? 2 : 1, invocations.GetArrayLength());
            var invocation = invocations[invocations.GetArrayLength() - 1];
            var input = invocation.GetProperty("inputs").EnumerateArray()
                .Single(member => member.GetProperty("name").GetString() == "receiver").GetProperty("value");
            var output = invocation.GetProperty("outputs").EnumerateArray()
                .Single(member => member.GetProperty("name").GetString() == "receiver").GetProperty("value");
            Assert.AreEqual("7", input.GetProperty("members")[0].GetProperty("value").GetProperty("value").GetString());
            Assert.AreEqual(expected, output.GetProperty("members")[0].GetProperty("value").GetProperty("value").GetString());
        }

        await InputIdleAsync(page);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
