using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons distinguish vectors from rank-one multidimensional arrays in values and reflected types.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Real workers preserve array kinds even when nested arrays have identical bounds and contents.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The array value or reflected type shape.</param>
    /// <param name="before">The expected original array type suffix.</param>
    /// <param name="after">The expected edited array type suffix.</param>
    [TestMethod]
    [DataRow("chromium", "array", "System.Int32[][]", "System.Int32[*][]")]
    [DataRow("webkit", "array", "System.Int32[][]", "System.Int32[*][]")]
    [DataRow("chromium", "bounds", "System.Int32[]", "System.Int32[*]")]
    [DataRow("webkit", "bounds", "System.Int32[]", "System.Int32[*]")]
    [DataRow("chromium", "type", "System.Int32[]", "System.Int32[*]")]
    [DataRow("webkit", "type", "System.Int32[]", "System.Int32[*]")]
    [DataRow("chromium", "jagged", "System.Int32[][]", "System.Int32[*][]")]
    [DataRow("webkit", "jagged", "System.Int32[][]", "System.Int32[*][]")]
    [DataRow("chromium", "generic", "System.Int32[]>", "System.Int32[*]>")]
    [DataRow("webkit", "generic", "System.Int32[]>", "System.Int32[*]>")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesArrayKinds(string browser, string kind, string before, string after)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ArrayKindExamples.Source(kind, false) + "\n.edit Read as Copy {\n"
            + ArrayKindExamples.Source(kind, true) + "\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        var originalResult = original.GetProperty("result");
        var editedResult = edited.GetProperty("result");
        var arrays = kind is "array" or "bounds";
        var property = arrays ? "type" : "value";
        Assert.EndsWith(before, originalResult.GetProperty(property).GetString()!);
        Assert.EndsWith(after, editedResult.GetProperty(property).GetString()!);
        if (arrays)
        {
            Assert.AreEqual("0:2", originalResult.GetProperty("value").GetString());
            Assert.AreEqual(kind == "bounds" ? "-1:2" : "0:2", editedResult.GetProperty("value").GetString());
            Assert.AreEqual(originalResult.GetProperty("members").GetRawText(), editedResult.GetProperty("members").GetRawText());
        }

        await ExpectComparisonTextAsync(page, "edited: completed");
        if (kind == "array")
        {
            await RunCorpusCellAsync(page, "call Read\nisinst int32[][]\nldnull\nceq\nret", 0);
            await RunCorpusCellAsync(page, "call Copy\nisinst int32[][]\nldnull\nceq\nret", 1);
        }

        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
