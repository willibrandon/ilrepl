namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons preserve null managed references and distinguish them from null stored values.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A scenario can pass and return null references while ordinary references still expose their stored values.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed browser worker and transcript assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesNullReferences(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .method object& Read(object& value) {
              ldarg.0
              ret
            }
            .edit Read as Copy {
              .method public static object& Read(object& value) cil managed {
                ldarg.0
                ret
              }
            }
            .method int32 Scenario() {
              .locals init (object value, int32 result)
              ldc.i4.0
              conv.i
              call Copy
              ldc.i4.0
              conv.i
              ceq
              stloc.1
              ldloca 0
              call Copy
              ldind.ref
              ldnull
              ceq
              ldloc.1
              add
              ret
            }
            """, "end of method Scenario");
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "edited: completed");
        var text = await ReadComparisonTranscriptAsync(page);
        Assert.Contains("Copy: match", text);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("call 1 argument 0: null reference", text);
        Assert.Contains("call 1 return: null reference", text);
        Assert.Contains("call 2 return: null", text);
        Assert.Contains("return: [System.Private.CoreLib]System.Int32 \"2\"", text);
        Assert.DoesNotContain("error:", text);
    }
}
