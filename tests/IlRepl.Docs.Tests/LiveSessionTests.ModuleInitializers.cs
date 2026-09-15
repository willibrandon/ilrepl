using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser copies, comparisons, and saved assemblies retain the source module's initialized state.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Module initialization remains separate from the source assembly and runs once in each copied runtime.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="typeInitializer">Whether the declaring type has its own static constructor.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesModuleInitialization(string browser, bool typeInitializer)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(ModuleInitializerFixture.Create(typeInitializer));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/module-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/module-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 Owner::Read()\nret", 142);
        await SubmitEditSourceAsync(page, ".edit int32 Owner::Read() as Copy {\n"
            + ModuleInitializerFixture.Method(false) + "\n}", "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call Copy\nret", 142);
        await RunCorpusCellAsync(page, "call Copy\nret", 142);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("142", side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + ModuleInitializerFixture.Method(true) + "\n}",
            "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var (index, expected) in new[] { (2, "142"), (3, "143") })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual(expected, side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: different");
        await RunCorpusCellAsync(page, "call Copy\nret", 143);
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/module-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/module-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/module-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [module-copy]IlRepl.Cell::Run()\nret", 143);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
