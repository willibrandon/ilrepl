namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser declarations cannot replace names already reserved by method edits.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A conflicting method is rejected while the original and edited methods remain callable.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed rejection and execution assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_MethodDeclarationCannotReuseAnEditName(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .method int32 Existing() {
              ldc.i4.s 41
              ret
            }
            .edit Existing as Copy {
              .method public static int32 Existing() cil managed {
                ldc.i4.s 42
                ret
              }
            }
            """, "edit Copy committed as revision 1");

        await SubmitEditSourceAsync(page, ".method int32 Copy() {\nldc.i4.s 99\nret\n}",
            "'Copy' already belongs to an edit; choose another method name");

        await ClearPromptAsync(page);
        await TypeLineAsync(page, ".clear");
        await RunCorpusCellAsync(page, "call Copy\nret", 42);
        await RunCorpusCellAsync(page, "call Existing\nret", 41);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
