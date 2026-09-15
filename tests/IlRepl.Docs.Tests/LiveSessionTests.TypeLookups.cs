using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser type lookups retain the original spelling of copied types in comparisons and saved assemblies.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Browser resolver overloads retain default lookup behavior and the exact strings supplied to explicit callbacks.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="resolver">The callback to supply, or none.</param>
    [TestMethod]
    [DataRow("chromium", "none")]
    [DataRow("webkit", "none")]
    [DataRow("chromium", "assembly")]
    [DataRow("webkit", "assembly")]
    [DataRow("chromium", "type")]
    [DataRow("webkit", "type")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesTypeLookupResolvers(string browser, string resolver)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, TypeLookupResolverExamples.Source(resolver, 2)
            + "\n.edit bool Lookup.Owner::Read() as Copy {\n" + TypeLookupResolverExamples.Method(resolver, 2) + "\n}",
            "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call bool Lookup.Owner::Read()\nconv.i4\nret", 1);
        await RunCorpusCellAsync(page, "call Copy\nconv.i4\nret", 1);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("true", side.GetProperty("result").GetProperty("value").GetString());
            Assert.AreEqual(resolver == "type" ? "Lookup.Owner\n" : "", side.GetProperty("standardOutput").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, "conv.i4");
        await TypeLineAsync(page, ".save /tmp/lookup-resolvers.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/lookup-resolvers.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/lookup-resolvers.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [lookup-resolvers]IlRepl.Cell::Run()\nret", 1);
    }

    /// <summary>
    /// Literal names resolve renamed owners and constructed types without changing the string printed by user code.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="shape">The type-name shape to resolve.</param>
    /// <param name="options">The number of Boolean lookup options.</param>
    [TestMethod]
    [DataRow("chromium", 0, 0)]
    [DataRow("webkit", 0, 0)]
    [DataRow("chromium", 3, 0)]
    [DataRow("webkit", 3, 0)]
    [DataRow("chromium", 4, 2)]
    [DataRow("webkit", 4, 2)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditTranslatesStringTypeLookups(string browser, int shape, int options)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, TypeLookupExamples.Source(shape, options, true)
            + "\n.edit bool Lookup.Owner::Read() as Copy {\n" + TypeLookupExamples.Method(shape, options, true, false) + "\n}",
            "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call bool Lookup.Owner::Read()\nconv.i4\nret", 1);
        await RunCorpusCellAsync(page, "call Copy\nconv.i4\nret", 1);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("true", side.GetProperty("result").GetProperty("value").GetString());
            Assert.AreEqual(TypeLookupExamples.Name(shape, options == 2) + "\n", side.GetProperty("standardOutput").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + TypeLookupExamples.Method(shape, options, true, true) + "\n}",
            "edit Copy committed as revision 2");
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
        await TypeLineAsync(page, ".save /tmp/type-lookups.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/type-lookups.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/type-lookups.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [type-lookups]IlRepl.Cell::Run()\nret", 0);
    }
}
