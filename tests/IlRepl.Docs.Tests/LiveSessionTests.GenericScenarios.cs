namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons explain how a parameterless scenario supplies generic arguments to an edited method.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Rejecting an open scenario preserves the session and a non-generic wrapper compares both closed calls.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_GenericScenarioRequiresClosedWrapper(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        await SubmitEditSourceAsync(page, """
            .class public Owner {
              .method public static int32 Read<T>() {
                ldc.i4.1
                ret
              }
            }
            .edit int32 Owner::Read<[1]>() as Copy {
              .method public static int32 Read<T>() {
                ldc.i4.2
                ret
              }
            }
            .method int32 Scenario() {
              call Copy<int32>
              ret
            }
            """, "end of method Scenario");
        await TypeLineAsync(page, ".method int32 Open<T>() { }");
        await ExpectCompletionAsync(page, "bad method name 'Open<T>'");
        await TypeLineAsync(page, ".compare Copy using Open");
        await ExpectCompletionAsync(page, "no session scenario 'Open'");
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: different");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString());
        Assert.AreEqual("1", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("2", edited.GetProperty("result").GetProperty("value").GetString());
        await RunCorpusCellAsync(page, "call Scenario\nret", 2);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
