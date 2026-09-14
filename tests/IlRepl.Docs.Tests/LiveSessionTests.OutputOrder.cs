using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons preserve managed and direct writes in each stream without adding line endings.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Byte-by-byte UTF-8 output and managed writes retain their actual order in both comparison workers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="error">Whether the writes use standard error.</param>
    /// <param name="reverse">Whether the edit reverses their order.</param>
    /// <returns>The completed stream and worker assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false, false)]
    [DataRow("webkit", false, false)]
    [DataRow("chromium", false, true)]
    [DataRow("webkit", false, true)]
    [DataRow("chromium", true, false)]
    [DataRow("webkit", true, false)]
    [DataRow("chromium", true, true)]
    [DataRow("webkit", true, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesMixedStreamOrder(string browser, bool error, bool reverse)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ComparisonOutputExamples.Source(error, false) + "\n.edit Work as Copy {\n"
            + ComparisonOutputExamples.Source(error, reverse) + "\n}", "edit Copy committed as revision 1");
        await TypeLineAsync(page, ".compare Copy ()");
        await ExpectComparisonTextAsync(page, reverse ? "Copy: different" : "Copy: match");
        var text = await BufferTextAsync(page);
        var stream = error ? "stderr: " : "stdout: ";
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains(stream + JsonSerializer.Serialize("\u00e9B"), text);
        Assert.Contains(stream + JsonSerializer.Serialize(reverse ? "B\u00e9" : "\u00e9B"), text);
        Assert.DoesNotContain(error ? "stdout: " : "stderr: ", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
