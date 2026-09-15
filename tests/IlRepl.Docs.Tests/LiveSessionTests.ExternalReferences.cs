namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons retain reference observations when the original method requires its external assembly.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// The framework original and an equivalent edit report matching reference writes in separate browser workers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed comparison and parent execution assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ExternalOriginalPreservesReferenceArguments(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .edit uint32 System.Threading.Interlocked::Increment(uint32&) as Copy {
              .method public static uint32 Increment(uint32& location) cil managed {
                ldarg.0
                ldarg.0
                ldind.u4
                ldc.i4.1
                add
                stind.i4
                ldarg.0
                ldind.u4
                ret
              }
            }
            """, "edit Copy committed as revision 1");

        await TypeLineAsync(page, ".compare Copy (41)");
        await ExpectComparisonTextAsync(page, "Copy: match");

        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"42\"", text);
        Assert.DoesNotContain("different-inputs", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        await RunCorpusCellAsync(page, ".locals init (uint32 number)\nldc.i4.s 41\nstloc.0\nldloca.s 0\ncall Copy\nconv.i4\nret", 42);
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
