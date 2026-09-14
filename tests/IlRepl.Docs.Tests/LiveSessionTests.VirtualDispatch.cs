using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons retain real virtual dispatch and observe only calls that reach the selected implementation.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A struct interface override keeps its receiver mutation in the scenario's original unboxed storage.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesConstrainedInterfaceStorage(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, VirtualComparisonExamples.Source(false, true) + "\n.edit "
            + VirtualComparisonExamples.Reference(false) + " as Copy {\n" + VirtualComparisonExamples.Method(false, true) + "\n}\n"
            + VirtualStructScenario.Source, "end of method Scenario");
        await RunCorpusCellAsync(page, "call Scenario\nret", 14);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.IsFalse(side.TryGetProperty("exception", out _), side.GetRawText());
            Assert.IsTrue(side.TryGetProperty("result", out var result), side.GetRawText());
            Assert.AreEqual("14", result.GetProperty("value").GetString());
            Assert.AreEqual(0, side.GetProperty("invocations").GetArrayLength());
        }

        await InputIdleAsync(page);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// Overrides, default interface bodies, and generic base calls preserve their results and invocation boundaries.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The scenario's call instruction or delegate binding.</param>
    /// <param name="behavior">The derived method's dispatch behavior.</param>
    /// <param name="generic">Whether the owner and selected method have type parameters.</param>
    [TestMethod]
    [DataRow("chromium", "virtual", "override", false)]
    [DataRow("webkit", "virtual", "override", false)]
    [DataRow("chromium", "constrained", "override", true)]
    [DataRow("webkit", "constrained", "override", true)]
    [DataRow("chromium", "delegate", "override", true)]
    [DataRow("webkit", "delegate", "override", true)]
    [DataRow("chromium", "virtual", "base", false)]
    [DataRow("webkit", "virtual", "base", false)]
    [DataRow("chromium", "constrained", "base", true)]
    [DataRow("webkit", "constrained", "base", true)]
    [DataRow("chromium", "delegate", "base", true)]
    [DataRow("webkit", "delegate", "base", true)]
    [DataRow("chromium", "virtual", "inherit", true)]
    [DataRow("webkit", "virtual", "inherit", true)]
    [DataRow("chromium", "delegate", "inherit", false)]
    [DataRow("webkit", "delegate", "inherit", false)]
    [DataRow("chromium", "virtual", "newslot", true)]
    [DataRow("webkit", "virtual", "newslot", true)]
    [DataRow("chromium", "virtual", "explicit", true)]
    [DataRow("webkit", "virtual", "explicit", true)]
    [DataRow("chromium", "call", "override", true)]
    [DataRow("webkit", "call", "override", true)]
    [DataRow("chromium", "virtual", "same", false)]
    [DataRow("webkit", "virtual", "same", false)]
    [DataRow("chromium", "delegate", "same", true)]
    [DataRow("webkit", "delegate", "same", true)]
    [DataRow("chromium", "virtual", "interface-inherit", true)]
    [DataRow("webkit", "virtual", "interface-inherit", true)]
    [DataRow("chromium", "constrained", "interface-inherit", false)]
    [DataRow("webkit", "constrained", "interface-inherit", false)]
    [DataRow("chromium", "delegate", "interface-inherit", false)]
    [DataRow("webkit", "delegate", "interface-inherit", false)]
    [DataRow("chromium", "virtual", "interface-override", true)]
    [DataRow("webkit", "virtual", "interface-override", true)]
    [DataRow("chromium", "constrained", "interface-override", false)]
    [DataRow("webkit", "constrained", "interface-override", false)]
    [DataRow("chromium", "delegate", "interface-override", false)]
    [DataRow("webkit", "delegate", "interface-override", false)]
    [DataRow("chromium", "virtual", "interface-explicit", false)]
    [DataRow("webkit", "virtual", "interface-explicit", false)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesVirtualDispatch(string browser, string kind, string behavior, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var interfaceType = behavior.StartsWith("interface-", StringComparison.Ordinal);
        await SubmitEditSourceAsync(page, VirtualComparisonExamples.Source(generic, interfaceType) + "\n.edit "
            + VirtualComparisonExamples.Reference(generic) + " as Copy {\n" + VirtualComparisonExamples.Method(generic, true) + "\n}\n"
            + VirtualComparisonExamples.Scenario(kind, behavior, generic), "end of method Scenario");
        if (interfaceType) behavior = behavior[10..];
        var selected = kind == "call" || behavior is "base" or "inherit" or "newslot";
        var added = kind != "call" && behavior == "base" ? 100 : 0;
        var overridden = behavior == "same" ? 8 : 107;
        await RunCorpusCellAsync(page, "call Scenario\nret", selected ? 9 + added : overridden);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        foreach (var (side, expected) in new[] { (original, 8), (edited, 9) })
        {
            Assert.IsFalse(side.TryGetProperty("exception", out _), side.GetRawText());
            Assert.IsTrue(side.TryGetProperty("result", out var result), side.GetRawText());
            Assert.AreEqual((selected ? expected + added : overridden).ToString(), result.GetProperty("value").GetString());
            var invocations = side.GetProperty("invocations");
            Assert.AreEqual(selected ? 1 : 0, invocations.GetArrayLength());
            if (selected)
            {
                Assert.AreEqual("completed", side.GetProperty("outcome").GetString());
                var returned = invocations[0].GetProperty("outputs").EnumerateArray()
                    .Single(member => member.GetProperty("name").GetString() == "return");
                Assert.AreEqual(expected.ToString(), returned.GetProperty("value").GetProperty("value").GetString());
            }
            else
            {
                Assert.AreEqual("the scenario did not invoke the selected method", side.GetProperty("detail").GetString());
            }
        }

        await InputIdleAsync(page);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
