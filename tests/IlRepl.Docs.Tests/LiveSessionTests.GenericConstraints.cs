namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser scenario capture rejects calls made incompatible by edited generic constraints.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Relaxing an edited constraint makes the copy callable without exporting an invalid original-side scenario.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed edited invocation and early rejection assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonRejectsChangedGenericConstraints(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .class public Choice {
              .method public static int32 Read<valuetype T>() {
                ldc.i4.s 41
                ret
              }
            }
            .edit int32 Choice::Read<[1]>() as Copy {
              .method public static int32 Read<T>() cil managed {
                ldc.i4.s 42
                ret
              }
            }
            .method int32 Scenario() {
              call Copy<string>
              ret
            }
            """, "end of method Scenario");
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectCompletionAsync(page, "the original and edited signatures must match");
        await InputIdleAsync(page);
        await EmptyPromptAsync(page);
        await RunCorpusCellAsync(page, "call Scenario\nret", 42);
        await RunCorpusCellAsync(page, "call int32 Choice::Read<int32>()\nret", 41);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
