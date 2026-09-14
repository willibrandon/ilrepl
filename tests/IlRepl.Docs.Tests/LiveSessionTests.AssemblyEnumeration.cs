using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits reject incomplete assembly type sets and retain the draft for an executable replacement.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Each enumeration API has a visible preflight error and a corrected copy can be compared and saved.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="api">The assembly or module enumeration API.</param>
    [TestMethod]
    [DataRow("chromium", "GetTypes")]
    [DataRow("webkit", "GetTypes")]
    [DataRow("chromium", "GetExportedTypes")]
    [DataRow("webkit", "GetExportedTypes")]
    [DataRow("chromium", "DefinedTypes")]
    [DataRow("webkit", "DefinedTypes")]
    [DataRow("chromium", "ExportedTypes")]
    [DataRow("webkit", "ExportedTypes")]
    [DataRow("chromium", "Module.GetTypes")]
    [DataRow("webkit", "Module.GetTypes")]
    [DataRow("chromium", "Module.FindTypes")]
    [DataRow("webkit", "Module.FindTypes")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditBlocksAssemblyEnumerationAndAcceptsReplacement(string browser, string api)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, AssemblyEnumerationExamples.Source(api), "end of class Owner");
        await RunCorpusCellAsync(page, "call int32 Owner::Read()\nret", 42);
        await SubmitEditSourceAsync(page, ".edit int32 Owner::Read() as Copy {\n" + AssemblyEnumerationExamples.Method(api) + "\n}",
            "complete type set");
        await PromptContainsAsync(page, "}");
        await ClearPromptAsync(page);
        await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 42\nret\n}\n}",
            "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("42", side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/enumeration-replacement.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/enumeration-replacement.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/enumeration-replacement.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [enumeration-replacement]IlRepl.Cell::Run()\nret", 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
