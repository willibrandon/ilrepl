namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits preserve catch types when a loaded assembly throws private exceptions.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Catch-only metadata is reported and still handles exceptions thrown by retained external code.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed loaded-assembly and worker assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesExternalCatchTypeIdentity(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .class private N.HiddenException extends Exception {
              .method public instance void .ctor() {
                ldarg.0
                call instance void Exception::.ctor()
                ret
              }
            }
            .class public N.Thrower {
              .method public static void Throw() {
                newobj instance void N.HiddenException::.ctor()
                throw
              }
            }
            .class public N.Fixture {
              .method public static int32 Read() {
                .locals init (int32 value)
                .try {
                  call void N.Thrower::Throw()
                  leave DONE
                } catch N.HiddenException {
                  pop
                  ldc.i4.s 41
                  stloc.0
                  leave DONE
                }
              DONE:
                ldloc.0
                ret
              }
            }
            """, "end of class N.Fixture");
        await TypeLineAsync(page, ".save /tmp/catch-types.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/catch-types.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/catch-types.dll");
        await ExpectCompletionAsync(page, "public types)");
        await SubmitEditSourceAsync(page, """
            .edit int32 [catch-types]N.Fixture::Read() as Copy {
              .method public static int32 Read() cil managed {
                .locals init (int32 value)
                .try {
                  call void [catch-types]N.Thrower::Throw()
                  leave DONE
                } catch [catch-types]N.HiddenException {
                  pop
                  ldc.i4.s 42
                  stloc.0
                  leave DONE
                }
              DONE:
                ldloc.0
                ret
              }
            }
            """, "edit Copy committed as revision 1");
        await TypeLineAsync(page, ".methods Copy");
        await ExpectCompletionAsync(page, ": catch HiddenException");
        await InputIdleAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        await ExpectComparisonTextAsync(page, "Copy: different");

        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"41\"", text);
        Assert.Contains("\"42\"", text);
        await RunCorpusCellAsync(page, "call Copy\nret", 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
