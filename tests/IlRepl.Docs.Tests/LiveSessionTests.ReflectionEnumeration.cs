using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser reflection sees the original member set through edits, comparisons, and saved copies.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Generated helpers leave static, instance, private, and generic owner method lists intact.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="instance">Whether the selected member has a receiver.</param>
    /// <param name="privateMethod">Whether the selected member is private.</param>
    /// <param name="generic">Whether its owner and method are generic.</param>
    [TestMethod]
    [DataRow("chromium", false, false, false)]
    [DataRow("webkit", false, false, false)]
    [DataRow("chromium", false, true, false)]
    [DataRow("webkit", false, true, false)]
    [DataRow("chromium", true, false, false)]
    [DataRow("webkit", true, false, false)]
    [DataRow("chromium", true, true, false)]
    [DataRow("webkit", true, true, false)]
    [DataRow("chromium", false, false, true)]
    [DataRow("webkit", false, false, true)]
    [DataRow("chromium", false, true, true)]
    [DataRow("webkit", false, true, true)]
    [DataRow("chromium", true, false, true)]
    [DataRow("webkit", true, false, true)]
    [DataRow("chromium", true, true, true)]
    [DataRow("webkit", true, true, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesTheReflectedMethodSet(string browser, bool instance, bool privateMethod, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ReflectionEnumerationExamples.Source(instance, privateMethod, generic) + "\n.edit "
            + ReflectionEnumerationExamples.Reference(instance, generic) + " as Copy {\n"
            + ReflectionEnumerationExamples.Method(instance, privateMethod, generic, true) + "\n}\n"
            + ReflectionEnumerationExamples.Scenario(instance, generic), "end of method Scenario");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        Assert.AreEqual("2", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("3", edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Scenario");
        await TypeLineAsync(page, ".save /tmp/member-set.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/member-set.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/member-set.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [member-set]IlRepl.Cell::Run()\nret", 3);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
