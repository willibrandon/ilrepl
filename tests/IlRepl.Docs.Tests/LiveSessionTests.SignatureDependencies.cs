using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Exact member signatures retain session-owned modifier types when browser edits are saved and reloaded.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A type named only in a member signature remains callable after a copied assembly is independently reloaded.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="shape">The signature position containing the required modifier.</param>
    /// <returns>The completed copy, comparison, export, and reload assertions.</returns>
    [TestMethod]
    [DataRow("chromium", "parameter")]
    [DataRow("webkit", "parameter")]
    [DataRow("chromium", "return")]
    [DataRow("webkit", "return")]
    [DataRow("chromium", "field")]
    [DataRow("webkit", "field")]
    [DataRow("chromium", "property")]
    [DataRow("webkit", "property")]
    [DataRow("chromium", "nested")]
    [DataRow("webkit", "nested")]
    [DataRow("chromium", "abstract")]
    [DataRow("webkit", "abstract")]
    [DataRow("chromium", "constructor")]
    [DataRow("webkit", "constructor")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditExportsSignatureDependencies(string browser, string shape)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, SignatureDependencyExamples.Source(shape, true), "end of class Owner");
        await TypeLineAsync(page, ".edit Owner::Read as Copy");
        await ExpectCompletionAsync(page, ".edit Owner::Read as Copy {");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        await TypeLineAsync(page, ".compare Copy (42)");
        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".method int32 Saved() {\nldc.i4.s 42\ncall Copy\nret\n}", "end of method Saved");
        await TypeLineAsync(page, "call Saved");
        await TypeLineAsync(page, ".save /tmp/signature-types.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/signature-types.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/signature-types.dll");
        await ExpectCompletionAsync(page, "public types)");
        await RunCorpusCellAsync(page, "call object [signature-types]IlRepl.Cell::Run()\nret", 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
