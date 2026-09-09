using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Lowercase, word-boundary and substring matches insert the binding spelling and execute in the browser.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_Matching_CorrectsAndRuns(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var cell = 1;
        foreach (var prefix in new[] { "environment::get_currentmanagedth", "Environment::gCMTI", "Environment::ManagedThreadI" })
        {
            await TypeLineAsync(page, ".clear");
            await page.Keyboard.TypeAsync("call " + prefix);
            await CompletionAtCaretAsync(page, $"il[{cell}]> call {prefix}", "members 1/1");
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(terminal).Not.ToContainTextAsync("members 1/1");
            await PromptAtCaretAsync(page, $"il[{cell}]> call Environment::get_CurrentManagedThreadId()");
            await page.Keyboard.PressAsync("Enter");
            await TypeLineAsync(page, "ret");
            await ExpectCompletionAsync(page, ": int32");
            cell++;
        }

        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Clicking or accepting the visible prediction inserts the same undoable operand and keeps terminal focus.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="acceptance">The input that accepts the selected row.</param>
    [TestMethod]
    [DataRow("chromium", "click")]
    [DataRow("webkit", "click")]
    [DataRow("chromium", "right")]
    [DataRow("webkit", "right")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_Acceptance_UndoesAndRuns(string browser, string acceptance)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        const string original = "call Environment::get_CurrentManagedTh";
        await page.Keyboard.TypeAsync(original);
        await CompletionAtCaretAsync(page, "il[1]> " + original, "members 1/1");
        if (acceptance == "click")
        {
            await ClickCompletionRowAsync(page);
        }
        else
        {
            await page.Keyboard.PressAsync("ArrowRight");
        }

        await Assertions.Expect(terminal).Not.ToContainTextAsync("members 1/1");
        await PromptAtCaretAsync(page, "il[1]> call Environment::get_CurrentManagedThreadId()");
        Assert.AreEqual("TEXTAREA", await page.EvaluateAsync<string>("() => document.activeElement.tagName"));
        await page.Keyboard.PressAsync("Control+z");
        await CompletionAtCaretAsync(page, "il[1]> " + original, "members 1/1");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(terminal).Not.ToContainTextAsync("members 1/1");
        await PromptAtCaretAsync(page, "il[1]> " + original);
        await page.Keyboard.PressAsync("Tab");
        await CompletionAtCaretAsync(page, "il[1]> " + original, "members 1/1");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, ": int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A mid-line correction preserves following text while Right without a prediction moves only the caret.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_MidReference_PreservesTrailingText(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        const string prefix = "call environment::get_currentmanagedth";
        const string suffix = " // retained";
        await page.Keyboard.TypeAsync(prefix + suffix);
        await page.Keyboard.PressAsync("Home");
        for (var index = 0; index < prefix.Length - 1; index++)
        {
            await page.Keyboard.PressAsync("ArrowRight");
        }

        await ExpectCompletionAsync(page, "members 1/1");
        await page.Keyboard.PressAsync("ArrowRight");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(terminal).Not.ToContainTextAsync("members 1/1");
        Assert.Contains(prefix + suffix, (await BufferRowsAsync(page))[^2]);
        await page.Keyboard.TypeAsync("x");
        await ExpectCompletionAsync(page, prefix + "x" + suffix);
        await page.Keyboard.PressAsync("Backspace");
        await ExpectCompletionAsync(page, "members 1/1");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(terminal).Not.ToContainTextAsync("members 1/1");
        Assert.Contains("call Environment::get_CurrentManagedThreadId()" + suffix, (await BufferRowsAsync(page))[^2]);
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, ": int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A nested generic argument returns completion to its outer parameter and the resulting type token executes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_NestedGenericCompletion_ReturnsToOuterParameter(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await page.Keyboard.TypeAsync("ldtoken Dictionary");
        await ExpectCompletionAsync(page, "❯ Dictionary<");
        await page.Keyboard.PressAsync("Tab");
        await ExpectCompletionAsync(page, "type argument 1 of 2 (TKey)");
        await page.Keyboard.TypeAsync("List");
        await ExpectCompletionAsync(page, "❯ List<");
        await page.Keyboard.PressAsync("Tab");
        await ExpectCompletionAsync(page, "type argument 1 of 1 (T)");
        await page.Keyboard.TypeAsync("str");
        await ExpectCompletionAsync(page, "❯ string");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">,");
        await ExpectCompletionAsync(page, "type argument 2 of 2 (TValue)");
        await page.Keyboard.TypeAsync("int32");
        await ExpectCompletionAsync(page, "❯ int32");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "Dictionary<List<string>, int32>");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A forward label remains in the cell across an unsent method declaration and the completed branch executes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ForwardLabel_CrossesAnUnsentDeclaration(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, "br EN\n.method void Separate() {\nINNER: ret\n}\nEND: ldc.i4.1\nret");
        await page.Keyboard.PressAsync("Control+Home");
        await page.Keyboard.PressAsync("End");
        await ExpectCompletionAsync(page, "labels 1/1");
        await ExpectCompletionAsync(page, "❯ END");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 1 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    private static Task ExpectCompletionAsync(IPage page, string text) =>
        Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync(text, new() { Timeout = 30_000 });

    /// <summary>
    /// Moving the palette selection changes the prediction that Right accepts and the selected overload executes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_Prediction_FollowsTheSelectedOverload(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, "ldc.i4.3");
        await TypeLineAsync(page, "ldc.i4.7");
        await page.Keyboard.TypeAsync("call Math::Ma");
        await ExpectCompletionAsync(page, "❯ Max(Decimal, Decimal)");
        Assert.Contains("call Math::Max(Decimal, Decimal)", (await BufferRowsAsync(page))[^2]);
        for (var index = 0; index < 4; index++)
        {
            await page.Keyboard.PressAsync("ArrowDown");
        }

        await ExpectCompletionAsync(page, "❯ Max(int32, int32)");
        Assert.Contains("call Math::Max(int32, int32)", (await BufferRowsAsync(page))[^2]);
        await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("members");
        Assert.Contains("call Math::Max(int32, int32)", (await BufferRowsAsync(page))[^2]);
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 7 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Editing a later label invalidates candidates at the same earlier caret and the new target executes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_FutureLabelEdit_RefreshesTheSamePrefix(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, "br EN\nEND: ldc.i4.1\nret");
        await page.Keyboard.PressAsync("Control+Home");
        await page.Keyboard.PressAsync("End");
        await ExpectCompletionAsync(page, "❯ END");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("labels 1/1");
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("Home");
        await page.Keyboard.PressAsync("Shift+End");
        await page.Keyboard.TypeAsync("ENDING: ldc.i4.7");
        await page.Keyboard.PressAsync("Control+Home");
        await page.Keyboard.PressAsync("End");
        await ExpectCompletionAsync(page, "❯ ENDING");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 7 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    private static async Task ClickCompletionRowAsync(IPage page)
    {
        var rows = await BufferRowsAsync(page);
        var row = Array.FindIndex(rows, value => value.Contains('❯'));
        Assert.IsGreaterThanOrEqualTo(0, row);
        var screen = await page.Locator(".xterm-screen").BoundingBoxAsync();
        Assert.IsNotNull(screen);
        var size = await page.EvaluateAsync<int[]>("() => [window.ilreplTerminal.cols, window.ilreplTerminal.rows]");
        await page.Mouse.ClickAsync(screen.X + screen.Width / size[0] * 5.5f, screen.Y + screen.Height / size[1] * (row + 0.5f));
    }
}
