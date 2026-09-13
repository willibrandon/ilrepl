namespace IlRepl.Docs.Tests;

/// <summary>
/// Direct browser comparisons resolve closed generic arguments declared in separate session assemblies.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Generic constructor calls use the caller's type parameter and preserve copied field initialization in browser workers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed constructor comparison and parent execution assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_DirectGenericComparisonPreservesConstructor(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .class public Constructed {
              .field public int32 Number
              .method public specialname rtspecialname instance void .ctor() {
                ldarg.0
                call instance void Object::.ctor()
                ldarg.0
                ldc.i4.s 41
                stfld int32 Constructed::Number
                ret
              }
            }
            .class public Factory {
              .method public static object Make<class .ctor T>() {
                call !!0 System.Activator::CreateInstance<!!0>()
                box !!0
                ret
              }
            }
            .edit object Factory::Make<Constructed>() as Made {
              .method public static object Make<class .ctor T>() cil managed {
                call !!0 System.Activator::CreateInstance<!!0>()
                box !!0
                dup
                castclass Constructed
                ldc.i4.s 42
                stfld int32 Constructed::Number
                ret
              }
            }
            """, "edit Made committed as revision 1");

        await TypeLineAsync(page, ".compare Made ()");
        await ExpectComparisonTextAsync(page, "Made: different");

        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"41\"", text);
        Assert.Contains("\"42\"", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        await RunCorpusCellAsync(page,
            "call Made\ncastclass IlRepl.Edits.Made.Type1\nldfld int32 IlRepl.Edits.Made.Type1::Number\nret", 42);
        await RunCorpusCellAsync(page,
            "call object Factory::Make<Constructed>()\ncastclass Constructed\nldfld int32 Constructed::Number\nret", 41);
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Both fresh workers close a generic method over a captured session struct while the parent retains its original and copy.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed generic comparison and parent execution assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_DirectGenericComparisonCapturesSessionStruct(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .class public sequential sealed Payload extends System.ValueType {
              .field public int32 Number
            }
            .class public Choice {
              .method public static int32 Size<T>() {
                sizeof !!0
                ret
              }
            }
            .edit int32 Choice::Size<Payload>() as Sized {
              .method public static int32 Size<T>() cil managed {
                sizeof !!0
                ldc.i4.1
                add
                ret
              }
            }
            """, "edit Sized committed as revision 1");

        await TypeLineAsync(page, ".compare Sized ()");
        await ExpectComparisonTextAsync(page, "Sized: different");

        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"4\"", text);
        Assert.Contains("\"5\"", text);
        Assert.DoesNotContain("setup-failed", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        await RunCorpusCellAsync(page, "call Sized\nret", 5);
        await RunCorpusCellAsync(page, "call int32 Choice::Size<Payload>()\nret", 4);
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
