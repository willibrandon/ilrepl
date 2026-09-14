using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser assembly and module lookups retain copied type names through comparisons and saved assemblies.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Generic receiver references retain their lookup result in live copies, workers, and saved assemblies.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="scope">The Assembly or Module receiver type.</param>
    [TestMethod]
    [DataRow("chromium", "Assembly")]
    [DataRow("webkit", "Assembly")]
    [DataRow("chromium", "Module")]
    [DataRow("webkit", "Module")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesConstrainedGenericTypeLookups(string browser, string scope)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ScopedTypeLookupExamples.TailSource(scope), "end of class Lookup.Owner");
        const string length = "callvirt instance string MemberInfo::get_Name()\ncallvirt instance int32 string::get_Length()";
        await RunCorpusCellAsync(page, "call class Type Lookup.Owner::Read()\n" + length + "\nret", 5);
        await SubmitEditSourceAsync(page, ".edit class Type Lookup.Owner::Read() as Copy {\n"
            + ScopedTypeLookupExamples.TailMethod(scope) + "\n}", "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call Copy\n" + length + "\nret", 5);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("[ilrepl.types.1]Lookup.Owner", side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/generic-lookups.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/generic-lookups.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/generic-lookups.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [generic-lookups]IlRepl.Cell::Run()\ncastclass Type\n" + length + "\nret", 5);
    }

    /// <summary>
    /// Instance lookups preserve receiver scope, case options, constructed types, and constrained calls.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="scope">The receiver used by the lookup.</param>
    /// <param name="shape">The type-name shape to resolve.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    /// <param name="constrained">Whether the call uses a managed reference to the receiver.</param>
    [TestMethod]
    [DataRow("chromium", "assembly", 0, 0, false)]
    [DataRow("webkit", "assembly", 0, 0, false)]
    [DataRow("chromium", "executing", 0, 2, false)]
    [DataRow("webkit", "executing", 0, 2, false)]
    [DataRow("chromium", "module", 4, 2, false)]
    [DataRow("webkit", "module", 4, 2, false)]
    [DataRow("chromium", "assembly", 0, 2, true)]
    [DataRow("webkit", "assembly", 0, 2, true)]
    [DataRow("chromium", "module", 0, 2, true)]
    [DataRow("webkit", "module", 0, 2, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditTranslatesScopedTypeLookups(string browser, string scope, int shape, int options, bool constrained)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ScopedTypeLookupExamples.Source(scope, shape, options, constrained)
            + "\n.edit bool Lookup.Owner::Read() as Copy {\n"
            + ScopedTypeLookupExamples.Method(scope, shape, options, constrained, false) + "\n}", "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call bool Lookup.Owner::Read()\nconv.i4\nret", 1);
        await RunCorpusCellAsync(page, "call Copy\nconv.i4\nret", 1);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("true", side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".edit Copy {\n"
            + ScopedTypeLookupExamples.Method(scope, shape, options, constrained, true) + "\n}", "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var (index, expected) in new[] { (2, "true"), (3, "false") })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual(expected, side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, "conv.i4");
        await TypeLineAsync(page, ".save /tmp/scoped-lookups.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/scoped-lookups.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/scoped-lookups.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [scoped-lookups]IlRepl.Cell::Run()\nret", 0);
    }
}
