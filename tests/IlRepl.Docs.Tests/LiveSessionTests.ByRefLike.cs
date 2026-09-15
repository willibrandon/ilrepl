namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser scenarios can consume spans while their byref-like observations remain explicitly unavailable.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Span arguments and returns execute in both workers without emitting an illegal box operation.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="returnsSpan">Whether the selected method returns a span instead of taking one by reference.</param>
    /// <returns>The completed scalar scenario and unavailable observation assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesSpanValues(string browser, bool returnsSpan)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var original = returnsSpan
            ? ".method public static valuetype Span<int32> Read() cil managed {\nldc.i4.3\nnewarr int32\n"
                + "newobj instance void valuetype Span<int32>::.ctor(int32[])\nret\n}"
            : ".method public static int32 Read(valuetype Span<int32>& value) cil managed {\nldarg.0\n"
                + "call instance int32 valuetype Span<int32>::get_Length()\nret\n}";
        var edited = returnsSpan ? original.Replace("ldc.i4.3", "ldc.i4.4", StringComparison.Ordinal)
            : original.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal);
        var scenario = ".method int32 Scenario() {\n.locals init (valuetype Span<int32> value)\n"
            + (returnsSpan ? "call Copy\nstloc.0\nldloca 0\ncall instance int32 valuetype Span<int32>::get_Length()\n"
                : "ldc.i4.3\nnewarr int32\nnewobj instance void valuetype Span<int32>::.ctor(int32[])\nstloc.0\nldloca 0\ncall Copy\n")
            + "ret\n}";
        await SubmitEditSourceAsync(page, original + "\n.edit Read as Copy {\n" + edited + "\n}\n" + scenario,
            "end of method Scenario");
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: incomplete");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("unavailable:", text);
        Assert.Contains("\"3\"", text);
        Assert.Contains("\"4\"", text);
        Assert.DoesNotContain("setup-failed", text);
        await RunCorpusCellAsync(page, "call Scenario\nret", 4);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
