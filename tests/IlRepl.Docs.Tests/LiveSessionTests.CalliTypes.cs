namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits pin types named only by standalone call signatures.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A calli modifier remains bound to its captured type while both workers execute the actual indirect call.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed dependency, replacement, and worker assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPinsCalliSignatureTypes(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .class public Marker {
              .field public int32 Original
            }
            .method int32 Read() {
              ldc.i4.1
              ldftn int32 Math::Sign(int32)
              calli int32 modopt(Marker)(int32)
              ret
            }
            .edit Read as Copy {
              .method public static int32 Read() cil managed {
                ldc.i4.1
                ldftn int32 Math::Sign(int32)
                calli int32 modopt(Marker)(int32)
                ret
              }
            }
            """, "edit Copy committed as revision 1");
        await SubmitEditSourceAsync(page, """
            .class public Marker {
              .field public int64 Added
            }
            .edit Copy {
              .method public static int32 Read() cil managed {
                ldc.i4.m1
                ldftn int32 Math::Sign(int32)
                calli int32 modopt(Marker)(int32)
                ret
              }
            }
            """, "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".methods Copy");
        await ExpectCompletionAsync(page, "copied (distinct type identity)");
        await InputIdleAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        await ExpectComparisonTextAsync(page, "Copy: different");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"1\"", text);
        Assert.Contains("\"-1\"", text);
        await RunCorpusCellAsync(page, "call Copy\nret", -1);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
