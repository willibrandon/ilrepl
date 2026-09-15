namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons preserve null task returns and the scenario branches that depend on them.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Direct calls retain null and a scenario follows its null branch without an invented task failure.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the selected method returns Task of int32.</param>
    /// <returns>The completed direct and scenario comparison assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", false)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_NullTaskComparisonPreservesNull(string browser, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var task = "class System.Threading.Tasks.Task" + (generic ? "`1<int32>" : "");
        await SubmitEditSourceAsync(page, ".method " + task + " Read() {\nldnull\nret\n}\n"
            + ".edit Read as Copy {\n.method public static " + task + " Read() cil managed {\nldnull\nret\n}\n}",
            "edit Copy committed as revision 1");

        await TypeLineAsync(page, ".compare Copy ()");
        await ExpectComparisonTextAsync(page, "Copy: match");
        var direct = await BufferTextAsync(page);
        Assert.Contains("original: completed", direct);
        Assert.Contains("edited: completed", direct);
        Assert.DoesNotContain("NullReferenceException", direct);

        await SubmitEditSourceAsync(page, """
            .method int32 Scenario() {
              call Copy
              brtrue.s present
              ldc.i4.s 42
              ret
            present:
              ldc.i4.0
              ret
            }
            """, "end of method Scenario");
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "\"42\"");

        var text = await BufferTextAsync(page);
        Assert.Contains("Copy: match", text);
        Assert.DoesNotContain("NullReferenceException", text);
        Assert.DoesNotContain("error:", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
