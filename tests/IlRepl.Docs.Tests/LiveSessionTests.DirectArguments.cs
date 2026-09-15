namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons validate the supplied literals against the original and edited signatures.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Removed parameters are rejected before worker startup while the edited method stays usable.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed arity and recovery assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_DirectComparisonChecksOriginalArity(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .method int32 Read(int32 value) {
              ldarg.0
              ret
            }
            .edit Read as Copy {
              .method public static int32 Read() cil managed {
                ldc.i4.s 42
                ret
              }
            }
            """, "edit Copy committed as revision 1");
        await TypeLineAsync(page, ".compare Copy ()");
        await ExpectComparisonTextAsync(page, "the original Copy requires 1 literal argument; received 0");
        Assert.DoesNotContain("setup-failed", await BufferTextAsync(page));
        await InputIdleAsync(page);
        await EmptyPromptAsync(page);
        await RunCorpusCellAsync(page, "call Copy\nret", 42);
        await RunCorpusCellAsync(page, "ldc.i4.s 41\ncall Read\nret", 41);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// A value outside the original parameter's range fails early while a value valid for both versions can run.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed literal validation and worker assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_DirectComparisonChecksOriginalLiteralType(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .method int32 Read(uint8 value) {
              ldarg.0
              ret
            }
            .edit Read as Copy {
              .method public static int32 Read(int32 value) cil managed {
                ldarg.0
                ret
              }
            }
            """, "edit Copy committed as revision 1");
        await TypeLineAsync(page, ".compare Copy (256)");
        await ExpectComparisonTextAsync(page, "original argument 1: '256' does not fit uint8:");
        await InputIdleAsync(page);
        await EmptyPromptAsync(page);
        await TypeLineAsync(page, ".compare Copy (42)");
        await ExpectComparisonTextAsync(page, "Copy: different-inputs");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"42\"", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
