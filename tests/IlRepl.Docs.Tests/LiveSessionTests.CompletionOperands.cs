using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Removing a pointer suffix excludes void from sizeof while restoring it produces an executable operand.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_TypeOperandCompletion_RejectsVoidStorage(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await page.Keyboard.TypeAsync("sizeof vo*");
        await page.Keyboard.PressAsync("ArrowLeft");
        await CompletionAtCaretAsync(page, "il[1]> sizeof vo", "❯ void");
        await page.Keyboard.PressAsync("Delete");
        await PromptAtCaretAsync(page, "il[1]> sizeof vo");
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("❯ void");
        await page.Keyboard.TypeAsync("*");
        await page.Keyboard.PressAsync("ArrowLeft");
        await CompletionAtCaretAsync(page, "il[1]> sizeof vo", "❯ void");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("End");
        await PromptAtCaretAsync(page, "il[1]> sizeof void*");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 4 : uint32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A generic starter preserves a comment before its existing bracket and keeps the selected method through execution.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_GenericStarter_PreservesSeparatedBracket(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        const string gap = " /* < */<";
        await page.Keyboard.TypeAsync("call Array::Em" + gap);
        for (var i = 0; i < gap.Length; i++)
        {
            await page.Keyboard.PressAsync("ArrowLeft");
        }

        await CompletionAtCaretAsync(page, "il[1]> call Array::Em", "members 1/1");
        await page.Keyboard.PressAsync("Tab");
        await PromptAtCaretAsync(page, "il[1]> call Array::Empty" + gap);
        await page.Keyboard.TypeAsync("str");
        await CompletionAtCaretAsync(page, "il[1]> call Array::Empty" + gap + "str", "❯ string");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">");
        await CompletionAtCaretAsync(page, "il[1]> call Array::Empty" + gap + "string>", "signatures 1/1");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ldlen");
        await TypeLineAsync(page, "conv.i4");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 0 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
