namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Explicit completion resumes immediately after a closed comment and the inserted type executes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ClosedComment_OffersTypes(string browser)
    {
        var launched = GetBrowser(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        const string line = "sizeof /* comment */";
        await page.Keyboard.TypeAsync(line);
        await PromptAtCaretAsync(page, "il[1]> " + line);
        await page.Keyboard.PressAsync("Tab");
        await CompletionAtCaretAsync(page, "il[1]> " + line, "bool");
        await page.Keyboard.TypeAsync("int3");
        await CompletionAtCaretAsync(page, "il[1]> " + line + "int3", "❯ int32");
        await page.Keyboard.PressAsync("Tab");
        await PromptAtCaretAsync(page, "il[1]> " + line + "int32");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 4 : uint32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A timer loads a library while the editor is idle and its previously empty completion refreshes automatically.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_BackgroundLoad_RefreshesIdleCompletion(string browser)
    {
        var launched = GetBrowser(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, """
            .class public Loading {
              .field public static class Timer Clock
              .method public static void Load(object ignored) {
                ldstr "/samples/Greeter.dll"
                call File::ReadAllBytes(string)
                call Assembly::Load(uint8[])
                pop
                ret
              }
            }
            ldnull
            ldftn void Loading::Load(object)
            newobj instance void TimerCallback::.ctor(object, native int)
            ldnull
            ldc.i4 10000
            ldc.i4.m1
            newobj instance void Timer::.ctor(TimerCallback, object, int32, int32)
            stsfld class Timer Loading::Clock
            ret
            """);
        await page.Keyboard.PressAsync("Enter");
        await EmptyPromptAsync(page);
        await TypeLineAsync(page, "ldstr \"world\"");
        const string line = "call Greeter.Hello::Sa";
        await page.Keyboard.TypeAsync(line);
        await PromptAtCaretAsync(page, "il[3]> " + line);
        Assert.DoesNotContain("❯ Say(", await BufferTextAsync(page));
        await CompletionAtCaretAsync(page, "il[3]> " + line, "❯ Say(string)");
        await page.Keyboard.PressAsync("Tab");
        await PromptAtCaretAsync(page, "il[3]> call Hello::Say(string)");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "Hello, world!");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
