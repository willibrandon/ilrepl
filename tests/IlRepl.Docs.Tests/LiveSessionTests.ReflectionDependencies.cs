using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits retain private members reached through runtime reflection.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Reflection remains executable in comparisons and saved copies after the session is reset.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="lookup">The reflection operation performed by the selected method.</param>
    [TestMethod]
    [DataRow("chromium", "runtime")]
    [DataRow("webkit", "runtime")]
    [DataRow("chromium", "method")]
    [DataRow("webkit", "method")]
    [DataRow("chromium", "enumeration")]
    [DataRow("webkit", "enumeration")]
    [DataRow("chromium", "property")]
    [DataRow("webkit", "property")]
    [DataRow("chromium", "constructor")]
    [DataRow("webkit", "constructor")]
    [DataRow("chromium", "nested")]
    [DataRow("webkit", "nested")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesReflectiveMembers(string browser, string lookup)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var source = ReflectionDependencyExamples.Source(lookup);
        var method = source[source.LastIndexOf(".method public static int32 Read()", StringComparison.Ordinal)..];
        method = method[..method.LastIndexOf('}')];
        await SubmitEditSourceAsync(page, source + "\n.edit int32 Owner::Read() as Copy {\n" + method + "\n}",
            "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call Copy\nret", 42);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + method.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal)
            + "\n}", "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await ExpectComparisonTextAsync(page, "call 1 return: [System.Private.CoreLib]System.Int32 \"42\"");
        await ExpectComparisonTextAsync(page, "call 1 return: [System.Private.CoreLib]System.Int32 \"43\"");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/reflected.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/reflected.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/reflected.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [reflected]IlRepl.Cell::Run()\nret", 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
