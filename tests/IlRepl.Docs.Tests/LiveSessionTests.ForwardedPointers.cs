namespace IlRepl.Docs.Tests;

/// <summary>
/// Private aliases retain generic function-pointer signatures in the browser and saved assemblies.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Owner and method arguments remain distinct when a private alias forwards function-pointer parameters and returns.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_PrivateGenericAliasForwardsFunctionPointers(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .class public Outer {
              .class nested private Hidden`1<T> {
                .method private static method !!U *(!!U) Read<U>(method !!U *(!!U) first, method !!U *(!!U) second) {
                  ldarg.0
                  ret
                }
              }
            }
            .edit method !!0 *(!!0) Outer/Hidden`1<int64>::Read<int32>(method !!0 *(!!0), method !!0 *(!!0)) as Copy {
              .method private static method !!U *(!!U) Read<U>(method !!U *(!!U) first, method !!U *(!!U) second) {
                ldarg.1
                ret
              }
            }
            .method int32 First(int32 value) {
              ldarg.0
              ldc.i4.1
              add
              ret
            }
            .method int32 Second(int32 value) {
              ldarg.0
              ldc.i4.2
              add
              ret
            }
            .method int32 Scenario() {
              ldc.i4.s 40
              ldftn First
              ldftn Second
              call Copy
              calli int32(int32)
              ret
            }
            """, "end of method Scenario");
        await RunCorpusCellAsync(page, "call Scenario\nret", 42);
        await TypeLineAsync(page, "call Scenario");
        await TypeLineAsync(page, ".save /tmp/forwarded-pointers.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/forwarded-pointers.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/forwarded-pointers.dll");
        await ExpectCompletionAsync(page, "public types)");
        await RunCorpusCellAsync(page, "call object [forwarded-pointers]IlRepl.Cell::Run()\nret", 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
