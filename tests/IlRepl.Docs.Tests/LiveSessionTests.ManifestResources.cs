using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser preflight rejects missing copied resources while the original retains its embedded data.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Real resource assemblies can be loaded, diagnosed, corrected, compared, and exported in the browser.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="api">The resource inspection API.</param>
    [TestMethod]
    [DataRow("chromium", "names")]
    [DataRow("webkit", "names")]
    [DataRow("chromium", "info")]
    [DataRow("webkit", "info")]
    [DataRow("chromium", "stream")]
    [DataRow("webkit", "stream")]
    [DataRow("chromium", "typed stream")]
    [DataRow("webkit", "typed stream")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditRejectsManifestResourceInspection(string browser, string api)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(ManifestResourceFixture.Create(api));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/resources.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/resources.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 Resources.Owner::Read()\nret", 42);
        await TypeLineAsync(page, ".edit int32 Resources.Owner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        await ExpectComparisonTextAsync(page, "assembly's resources");
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
        await TypeLineAsync(page, ".save /tmp/resource-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/resource-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/resource-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [resource-copy]IlRepl.Cell::Run()\nret", 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
