using System.Diagnostics;
using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    /// <summary>
    /// The browser discovers a framework member, keeps Tab in the terminal and executes the accepted operand.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_OperandCompletion_AcceptsAndRuns(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        await page.Keyboard.TypeAsync("call Environment::get_CurrentManagedTh");
        var watch = Stopwatch.StartNew();
        // Assertion retries back off up to a second; sample actual rendering at frame cadence instead.
        await page.WaitForFunctionAsync("() => document.querySelector('#terminal').textContent.includes('members 1/1')",
            null, new() { PollingInterval = 16, Timeout = 30_000 });
        TestContext.WriteLine($"First framework member page in {browser}: {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.IsLessThan(TimeSpan.FromSeconds(1), watch.Elapsed);
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(terminal).Not.ToContainTextAsync("members 1/1");
        Assert.AreEqual("TEXTAREA", await page.EvaluateAsync<string>("() => document.activeElement.tagName"));
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync(": int32", new() { Timeout = 30_000 });
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Completion sees an unsent method's named argument and the completed block executes unchanged.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_UnsentMethodArgument_CompletesAndRuns(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        await PasteAsync(page, ".method int32 Twice(int32 number) {\n  ldarg num");
        await Assertions.Expect(terminal).ToContainTextAsync("arguments 1/1", new() { Timeout = 30_000 });
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(terminal).Not.ToContainTextAsync("arguments 1/1");
        await PasteAsync(page, "\n  ldc.i4.2\n  mul\n  ret\n}");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Twice", new() { Timeout = 30_000 });
        await TypeLineAsync(page, "ldc.i4.3");
        await TypeLineAsync(page, "call Twice");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 6 : int32", new() { Timeout = 30_000 });
    }

    /// <summary>
    /// A generic method keeps its selected owner through argument completion and parameter-list insertion.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_GenericCompletion_BindsAtTheEnd(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        await page.Keyboard.TypeAsync("call Array::Empt");
        await Assertions.Expect(terminal).ToContainTextAsync("members 1/1", new() { Timeout = 30_000 });
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(terminal).ToContainTextAsync("type argument 1 of 1 (T)", new() { Timeout = 30_000 });
        await page.Keyboard.TypeAsync("str");
        await Assertions.Expect(terminal).ToContainTextAsync("❯ string", new() { Timeout = 30_000 });
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">");
        await Assertions.Expect(terminal).ToContainTextAsync("signatures 1/1", new() { Timeout = 30_000 });
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ldlen");
        await TypeLineAsync(page, "conv.i4");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 0 : int32", new() { Timeout = 30_000 });
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
