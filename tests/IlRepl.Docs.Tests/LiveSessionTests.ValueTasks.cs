using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser scenarios observe the original value-task representation instead of an instrumentation replacement.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Inline and task-backed value tasks remain distinguishable through AsTask in both browser runtimes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the value task returns an integer.</param>
    /// <returns>The completed representation and comparison assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesValueTaskRepresentation(string browser, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ValueTaskComparisonExamples.Source(generic, false) + "\n.edit Read as Copy {\n"
            + ValueTaskComparisonExamples.Source(generic, true) + "\n}\n" + ValueTaskComparisonExamples.Scenario(generic),
            "end of method Scenario");
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: different");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("System.Boolean \"true\"", text);
        Assert.Contains("System.Boolean \"false\"", text);
        Assert.DoesNotContain("unavailable", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// A pooled channel value task is consumed only by its caller and observed when returned from the scenario.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="returnSource">Whether the scenario returns the original value task for the worker to await.</param>
    /// <returns>The completed source-consumption and observation assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesPooledValueTask(string browser, bool returnSource)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        const string valueTask = "valuetype System.Threading.Tasks.ValueTask`1<bool>";
        var source = ".class public Pooled {\n.field public static " + valueTask + " Pending\n.method public static " + valueTask
            + " Read() {\nldsfld " + valueTask + " Pooled::Pending\nret\n}\n}\n.edit " + valueTask + " Pooled::Read() as Copy {\n"
            + ".method public static " + valueTask + " Read() cil managed {\nldsfld " + valueTask + " Pooled::Pending\nret\n}\n}\n";
        var scenario = ".method " + (returnSource ? valueTask : "bool") + " Scenario() {\n" + ValueTaskComparisonExamples.PooledSetup()
            + "stsfld " + valueTask + " IlRepl.Edits.Copy.Owner::Pending\ncall Copy\n"
            + (returnSource ? "" : "stloc.2\nldloca 2\ncall instance !0 " + valueTask + "::get_Result()\n") + "ret\n}";
        await SubmitEditSourceAsync(page, source + scenario, "end of method Scenario");
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, returnSource ? "Copy: match" : "Copy: incomplete");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("return: [System.Private.CoreLib]System.Boolean \"true\"", text);
        Assert.DoesNotContain("threw", text);
        if (!returnSource)
        {
            Assert.Contains("unavailable", text);
        }
    }
}
