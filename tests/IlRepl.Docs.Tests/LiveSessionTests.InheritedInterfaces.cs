using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser copies retain interface inheritance and explicit implementations through comparisons and exports.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Copied derived types dispatch to the original interface implementation before and after editing their caller.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="behavior">The derived type's relationship to the interface.</param>
    /// <param name="generic">Whether the owner and interface are generic.</param>
    /// <param name="expected">The implementation selected by the source declaration.</param>
    [TestMethod]
    [DataRow("chromium", "inherit", false, 1)]
    [DataRow("webkit", "inherit", false, 1)]
    [DataRow("chromium", "inherit", true, 1)]
    [DataRow("webkit", "inherit", true, 1)]
    [DataRow("chromium", "reimplement", true, 2)]
    [DataRow("webkit", "reimplement", true, 2)]
    [DataRow("chromium", "override", true, 2)]
    [DataRow("webkit", "override", true, 2)]
    [DataRow("chromium", "explicit", true, 2)]
    [DataRow("webkit", "explicit", true, 2)]
    [DataRow("chromium", "class-explicit", true, 2)]
    [DataRow("webkit", "class-explicit", true, 2)]
    [DataRow("chromium", "interface-inherit", true, 1)]
    [DataRow("webkit", "interface-inherit", true, 1)]
    [DataRow("chromium", "overload", true, 2)]
    [DataRow("webkit", "overload", true, 2)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesInheritedInterfaceDispatch(string browser, string behavior, bool generic, int expected)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, InheritedInterfaceExamples.Source(behavior, generic), "end of class Owner");
        await RunCorpusCellAsync(page, "call " + InheritedInterfaceExamples.Reference(generic) + "\nret", expected);
        await SubmitEditSourceAsync(page, ".edit " + InheritedInterfaceExamples.Reference(generic) + " as Copy {\n"
            + InheritedInterfaceExamples.Method(behavior, generic, false) + "\n}", "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call Copy\nret", expected);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual(expected.ToString(), side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + InheritedInterfaceExamples.Method(behavior, generic, true) + "\n}",
            "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var (index, value) in new[] { (2, expected), (3, expected + 10) })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual(value.ToString(), side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/interface-slots.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/interface-slots.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/interface-slots.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [interface-slots]IlRepl.Cell::Run()\nret", expected + 10);
    }
}
