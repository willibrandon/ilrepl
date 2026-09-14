using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser copies activate their renamed types through assembly receivers and retain constructor state.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Assembly activation retains overload options, copied generic identity, and constructor arguments through export.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="overload">The number of activation parameters.</param>
    /// <param name="nested">Whether to activate a nested generic owner.</param>
    /// <param name="dispatch">The call, callvirt, or constrained tail dispatch.</param>
    [TestMethod]
    [DataRow("chromium", 1, false, "callvirt")]
    [DataRow("webkit", 1, false, "callvirt")]
    [DataRow("chromium", 2, true, "callvirt")]
    [DataRow("webkit", 2, true, "callvirt")]
    [DataRow("chromium", 7, false, "callvirt")]
    [DataRow("webkit", 7, false, "callvirt")]
    [DataRow("chromium", 7, true, "callvirt")]
    [DataRow("webkit", 7, true, "callvirt")]
    [DataRow("chromium", 7, true, "call")]
    [DataRow("webkit", 7, true, "call")]
    [DataRow("chromium", 7, true, "tail")]
    [DataRow("webkit", 7, true, "tail")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditTranslatesAssemblyActivation(string browser, int overload, bool nested, string dispatch)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, AssemblyActivationExamples.Source(overload, nested, dispatch), "end of class Activation.Owner");
        var name = ActivationExamples.Name(nested, overload != 1);
        var quoted = "\"" + name + "\"";
        await RunCorpusCellAsync(page, "ldstr " + quoted + "\ncall int32 Activation.Owner::Read(string)\nret", 42);
        await SubmitEditSourceAsync(page, ".edit int32 Activation.Owner::Read(string) as Copy {\n"
            + AssemblyActivationExamples.Method(overload, nested, dispatch, false) + "\n}", "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "ldstr " + quoted + "\ncall Copy\nret", 42);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy (" + quoted + ")");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("42", side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + AssemblyActivationExamples.Method(overload, nested, dispatch, true) + "\n}",
            "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy (" + quoted + ")");
        foreach (var (index, expected) in new[] { (2, "42"), (3, "43") })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual(expected, side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "ldstr " + quoted);
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/assembly-activation.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/assembly-activation.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/assembly-activation.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [assembly-activation]IlRepl.Cell::Run()\nret", 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
