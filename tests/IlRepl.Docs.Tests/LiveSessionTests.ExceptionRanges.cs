using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Tab-separated exception ranges submit and execute through the published browser editor.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Pasted ranges submit on Enter, commit as edits, and execute their protected bodies and handlers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The exception clause kind.</param>
    /// <returns>The completed browser editor, comparison, and execution assertions.</returns>
    [TestMethod]
    [DataRow("chromium", "catch")]
    [DataRow("webkit", "catch")]
    [DataRow("chromium", "filter")]
    [DataRow("webkit", "filter")]
    [DataRow("chromium", "finally")]
    [DataRow("webkit", "finally")]
    [DataRow("chromium", "fault")]
    [DataRow("webkit", "fault")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_TabbedExceptionRangesSubmitAndExecute(string browser, string kind)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var source = ExceptionRangeExamples.Source(kind, "\t");
        await SubmitEditSourceAsync(page, source, "end of method Work");
        await SubmitEditSourceAsync(page, ".edit Work as Copy {\n" + source + "\n}", "edit Copy committed as revision 1");
        await TypeLineAsync(page, ".compare Copy (1)");
        await ExpectComparisonTextAsync(page, "Copy: match");
        await RunCorpusCellAsync(page, "ldc.i4.1\ncall Copy\nret", 42);
        await RunCorpusCellAsync(page, "ldc.i4.0\ncall Work\nret", kind == "finally" ? 42 : 40);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
