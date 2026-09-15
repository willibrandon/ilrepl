namespace IlRepl.Docs.Tests;

/// <summary>
/// Quoted scenario identifiers remain usable in browser comparisons, ordinary calls, and saved assemblies.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Quoted names select their declared scenario and leave following input and timeout options intact.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="identifier">The quoted CIL identifier.</param>
    /// <param name="name">The declared metadata name.</param>
    [TestMethod]
    [DataRow("chromium", "'My Scenario'", "My Scenario")]
    [DataRow("webkit", "'My Scenario'", "My Scenario")]
    [DataRow("chromium", "'Scenario'", "Scenario")]
    [DataRow("webkit", "'Scenario'", "Scenario")]
    [DataRow("chromium", "'Owner\\'s Scenario'", "Owner's Scenario")]
    [DataRow("webkit", "'Owner\\'s Scenario'", "Owner's Scenario")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonUsesQuotedScenarioNames(string browser, string identifier, string name)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ".method int32 Read(int32 value) {\nldarg.0\nret\n}\n.edit Read as Copy {\n"
            + ".method public static int32 Read(int32 value) {\nldarg.0\nldc.i4.1\nadd\nret\n}\n}\n"
            + ".method int32 " + identifier + "() {\nldc.i4.s 42\ncall Copy\nret\n}", "end of method " + name);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using " + identifier + " --stdin \"42\\n\" --timeout 10s --assert");
        foreach (var (index, expected) in new[] { (0, "42"), (1, "43") })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual(expected, side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: different");
        await RunCorpusCellAsync(page, "call " + identifier + "\nret", 43);
        await TypeLineAsync(page, "call " + identifier);
        await TypeLineAsync(page, ".save /tmp/quoted-scenario.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/quoted-scenario.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/quoted-scenario.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [quoted-scenario]IlRepl.Cell::Run()\nret", 43);
    }
}
