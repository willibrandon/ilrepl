using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Opens the built docs site in a real browser and drives the live session, which runs the
/// engine and the Hex1b UI on the .NET WebAssembly runtime.
/// </summary>
[TestClass]
public sealed partial class LiveSessionTests
{
    private static StaticSite? s_site;
    private static IPlaywright? s_playwright;

    [System.Text.RegularExpressions.GeneratedRegex("sending [1-9][0-9]*/3002")]
    private static partial System.Text.RegularExpressions.Regex StartedLongSubmission();

    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Serves the build output and starts a headless browser once for the class.
    /// </summary>
    /// <param name="context">The class initialization context.</param>
    /// <returns>A task that completes when the server and browser are up.</returns>
    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Directory.Exists(SitePaths.Dist) || !File.Exists(Path.Combine(SitePaths.Dist, "try", "_framework", "dotnet.js")))
        {
            Assert.Inconclusive("build the docs with the browser assets first: dotnet run --file scripts/Publish-Wasm.cs && pnpm --dir docs build");
        }

        s_site = await StaticSite.StartAsync(SitePaths.Dist).ConfigureAwait(false);
        s_playwright = await Playwright.CreateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the browser and the server.
    /// </summary>
    /// <returns>A task that completes when both are gone.</returns>
    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        s_playwright?.Dispose();
        if (s_site is not null)
        {
            await s_site.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The runtime boots, the UI renders, and a cell compiles and runs, in Chromium and in WebKit.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_RunsCellInBrowser(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/try/");
        await page.WaitForFunctionAsync("() => window.ilreplReady === true", null, new PageWaitForFunctionOptions { Timeout = 180_000 });
        await Assertions.Expect(page.Locator("#session-status")).ToHaveTextAsync("Ready");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("il[1]>", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        // The layout must match the terminal's size: the prompt sits on the second to last row and the
        // status bar on the last, otherwise they are below the visible rows.
        var rows = await BufferRowsAsync(page);
        Assert.StartsWith("il[1]>", rows[^2].TrimStart(), "the prompt should be on the second to last row");
        Assert.Contains("Ctrl+Q", rows[^1], "the status bar should be on the last row");

        // The rows must also be painted where the buffer says they are: the page's own styles must
        // not push xterm's row elements apart, or the bottom rows end up outside the box.
        var layout = await page.EvaluateAsync<int[]>(
            "() => { const rows = Array.from(document.querySelectorAll('.xterm-rows > div')); const box = document.getElementById('terminal'); return [rows.length, rows[rows.length - 1].offsetTop + rows[rows.length - 1].offsetHeight, box.clientHeight]; }");
        Assert.AreEqual(rows.Length, layout[0], "xterm should have one element per row");
        Assert.IsLessThanOrEqualTo(layout[2], layout[1], "the last row must be painted inside the terminal box");

        // Click into the transcript the way a person does, then type.
        var box = await page.Locator("#terminal").BoundingBoxAsync();
        Assert.IsNotNull(box);
        await page.Mouse.ClickAsync(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        foreach (var line in new[] { "ldc.i4 6", "ldc.i4 7", "mul", "ret" })
        {
            await page.Keyboard.TypeAsync(line);
            await page.Keyboard.PressAsync("Enter");
        }

        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 42 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        var text = await BufferTextAsync(page);
        Assert.Contains("┊ [int32, int32] ◂ top", text);
        Assert.Contains("il[2]>", text);
    }

    /// <summary>
    /// After focus has left the page's terminal, a click anywhere in the box brings it back, the
    /// padding around the rows included, and typing reaches the prompt again.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task ClickInBox_RestoresFocusToPrompt(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);

        await page.Locator("h1").ClickAsync();
        Assert.AreEqual("BODY", await page.EvaluateAsync<string>("() => document.activeElement.tagName"), "clicking the heading should take focus off the terminal");

        var box = await page.Locator("#terminal").BoundingBoxAsync();
        Assert.IsNotNull(box);
        await page.Mouse.ClickAsync(box.X + 3, box.Y + 3);
        Assert.AreEqual("TEXTAREA", await page.EvaluateAsync<string>("() => document.activeElement.tagName"), "a click in the padding should focus the terminal");
        await TypeLineAsync(page, "ldc.i4 6");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("┊ [int32]", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        await page.Locator("h1").ClickAsync();
        await page.Mouse.ClickAsync(box.X + box.Width - 4, box.Y + box.Height - 4);
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 6 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// In the browser, selection and copy are the terminal's own: a drag across the transcript
    /// selects in the terminal and y, the desktop's yank, or Ctrl+C copies it, the app never
    /// enters copy mode, the wheel still scrolls the transcript, and the caret is a blinking block.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Drag_SelectsInTheTerminal_AndWheelScrolls(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched, clipboard: browser == "chromium");
        var page = await OpenSessionAsync(context);
        var cursor = await page.EvaluateAsync<string[]>("() => [window.ilreplTerminal.options.cursorStyle, String(window.ilreplTerminal.options.cursorBlink)]");
        Assert.AreEqual("block", cursor[0], "the caret should be a block");
        Assert.AreEqual("true", cursor[1], "the caret should blink");

        await TypeLineAsync(page, "ldc.i4 6");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("┊ [int32]", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        // Drag across the echoed line on the second row: the terminal selects it, the app does not.
        var screen = await page.Locator(".xterm-screen").BoundingBoxAsync();
        Assert.IsNotNull(screen);
        var size = await page.EvaluateAsync<int[]>("() => [window.ilreplTerminal.cols, window.ilreplTerminal.rows]");
        var cellWidth = screen.Width / size[0];
        var cellHeight = screen.Height / size[1];
        var y = screen.Y + (cellHeight * 1.5f);
        await page.Mouse.MoveAsync(screen.X + (cellWidth * 0.5f), y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(screen.X + (cellWidth * 30.5f), y, new MouseMoveOptions { Steps = 8 });
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("() => window.ilreplTerminal.hasSelection()", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        Assert.Contains("ldc.i4 6", await page.EvaluateAsync<string>("() => window.ilreplTerminal.getSelection()"), "the terminal should hold the selection");
        Assert.DoesNotContain("y yank", await page.Locator("#terminal").InnerTextAsync(), "the app should not enter copy mode");

        // y copies the terminal's selection and clears it, as it yanks on the desktop, and no y
        // reaches the prompt.
        await page.Keyboard.PressAsync("y");
        await page.WaitForFunctionAsync("() => typeof window.ilreplLastCopy === 'string'", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        Assert.Contains("ldc.i4 6", await page.EvaluateAsync<string>("() => window.ilreplLastCopy"), "the selected row should be what was copied");
        await page.WaitForFunctionAsync("() => !window.ilreplTerminal.hasSelection()", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        Assert.DoesNotContain("il[1]> y", await page.Locator("#terminal").InnerTextAsync(), "y should copy, not type");

        // Ctrl+C does the same.
        await page.EvaluateAsync("() => { window.ilreplLastCopy = null; }");
        await page.Mouse.MoveAsync(screen.X + (cellWidth * 0.5f), y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(screen.X + (cellWidth * 30.5f), y, new MouseMoveOptions { Steps = 8 });
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("() => window.ilreplTerminal.hasSelection()", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        await page.Keyboard.PressAsync("Control+c");
        await page.WaitForFunctionAsync("() => typeof window.ilreplLastCopy === 'string'", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        Assert.Contains("ldc.i4 6", await page.EvaluateAsync<string>("() => window.ilreplLastCopy"), "the selected row should be what was copied");
        await page.WaitForFunctionAsync("() => !window.ilreplTerminal.hasSelection()", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        if (browser == "chromium")
        {
            Assert.Contains("ldc.i4 6", await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"), "the copy should be on the clipboard");
        }

        // Shift+Up is the desktop's; here the prompt is untouched and typing goes on.
        await page.Keyboard.PressAsync("Shift+ArrowUp");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 6 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        Assert.DoesNotContain("y yank", await page.Locator("#terminal").InnerTextAsync(), "the app should not enter copy mode");

        // The help is taller than the box; the wheel scrolls the transcript up and back down.
        await TypeLineAsync(page, ".help");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("Ctrl+Q leaves.", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await page.Mouse.MoveAsync(screen.X + (screen.Width / 2), screen.Y + (screen.Height / 3));
        await page.Mouse.WheelAsync(0, -1000);
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("Ctrl+Q leaves.", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        await page.Mouse.WheelAsync(0, 1000);
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("Ctrl+Q leaves.", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
    }

    /// <summary>
    /// A click on a row of the palette takes that suggestion in the browser too: the page sends
    /// a plain click to the app while a drag stays with the terminal.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Click_OnAPaletteRow_TakesIt(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await page.Keyboard.TypeAsync("ldc.i4.");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("❯ ldc.i4.0", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        var row = await page.EvaluateAsync<int>("() => { const t = window.ilreplTerminal; for (let i = 0; i < t.rows; i++) { const l = t.buffer.active.getLine(i); if (l && l.translateToString(true).includes(' ldc.i4.2 ')) return i; } return -1; }");
        Assert.IsGreaterThanOrEqualTo(0, row, "the palette should list ldc.i4.2");
        var screen = await page.Locator(".xterm-screen").BoundingBoxAsync();
        Assert.IsNotNull(screen);
        var size = await page.EvaluateAsync<int[]>("() => [window.ilreplTerminal.cols, window.ilreplTerminal.rows]");
        var cellWidth = screen.Width / size[0];
        var cellHeight = screen.Height / size[1];
        await page.Mouse.ClickAsync(screen.X + (cellWidth * 4.5f), screen.Y + (cellHeight * (row + 0.5f)));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("il[1]> ldc.i4.2", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("opcodes", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("┊ [int32]", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// Without the clipboard API, as on plain http or with permission denied, a copy still lands
    /// through the terminal's own copy handler, and a selection is cleared only once it has.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Copy_WithoutClipboardApi_StillCopies(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        // The page script is served with the clipboard API taken away ahead of it.
        await context.RouteAsync("**/try/main.js", async route =>
        {
            var response = await route.FetchAsync();
            var body = await response.TextAsync();
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Response = response,
                Body = "Object.defineProperty(navigator, 'clipboard', { value: undefined });\n" + body,
                ContentType = "text/javascript",
            });
        });
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, "ldc.i4 6");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("┊ [int32]", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        var screen = await page.Locator(".xterm-screen").BoundingBoxAsync();
        Assert.IsNotNull(screen);
        var size = await page.EvaluateAsync<int[]>("() => [window.ilreplTerminal.cols, window.ilreplTerminal.rows]");
        var cellWidth = screen.Width / size[0];
        var cellHeight = screen.Height / size[1];
        var y = screen.Y + (cellHeight * 1.5f);
        await page.Mouse.MoveAsync(screen.X + (cellWidth * 0.5f), y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(screen.X + (cellWidth * 30.5f), y, new MouseMoveOptions { Steps = 8 });
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("() => window.ilreplTerminal.hasSelection()", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        await page.Keyboard.PressAsync("y");
        await page.WaitForFunctionAsync("() => typeof window.ilreplLastCopy === 'string'", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        Assert.Contains("ldc.i4 6", await page.EvaluateAsync<string>("() => window.ilreplLastCopy"), "the selected row should be what was copied");
        await page.WaitForFunctionAsync("() => !window.ilreplTerminal.hasSelection()", null, new PageWaitForFunctionOptions { Timeout = 10_000 });
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 6 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        Assert.DoesNotContain("il[1]> y", await page.Locator("#terminal").InnerTextAsync(), "y should copy, not type");
    }

    /// <summary>
    /// Ctrl+Q ends the session and a fresh one starts in the same runtime with an empty transcript.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task LiveSession_RestartsAfterQuit(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, "ldc.i4 6");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("┊ [int32]", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        await page.Keyboard.PressAsync("Control+q");
        await WaitForSessionAsync(page, 2, 60_000);

        Assert.AreEqual("quit", await page.EvaluateAsync<string>("() => window.ilreplLastRestart"));
        var text = await BufferTextAsync(page);
        Assert.Contains("il[1]>", text, "the new session should start at cell 1");
        Assert.DoesNotContain("ldc.i4 6", text, "the new session should have an empty transcript");
        await TypeLineAsync(page, "ldc.i4 3");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 3 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// The restart button replaces the worker; the runtime boots again into a fresh session.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task RestartButton_StartsFreshSession(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, "ldc.i4 6");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("┊ [int32]", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        await page.Locator("#session-restart").ClickAsync();
        await WaitForSessionAsync(page, 2, 180_000);

        Assert.AreEqual("button", await page.EvaluateAsync<string>("() => window.ilreplLastRestart"));
        var text = await BufferTextAsync(page);
        Assert.Contains("il[1]>", text, "the new session should start at cell 1");
        Assert.DoesNotContain("ldc.i4 6", text, "the new session should have an empty transcript");
        await ClickIntoTerminalAsync(page);
        await TypeLineAsync(page, "ldc.i4 4");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 4 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// A cell that never returns blocks the runtime. The page notices the missing heartbeats and
    /// restarts the worker on its own.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task Watchdog_RestartsHungSession(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, "L: br L");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("┊ []", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await TypeLineAsync(page, "ret");

        await WaitForSessionAsync(page, 2, 180_000);

        Assert.AreEqual("hung", await page.EvaluateAsync<string>("() => window.ilreplLastRestart"));
        Assert.Contains("il[1]>", await BufferTextAsync(page), "the new session should start at cell 1");
        await ClickIntoTerminalAsync(page);
        await TypeLineAsync(page, "ldc.i4 5");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 5 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// The home page links to the live session and shows the install command.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Home_LinksToLiveSessionAndInstall()
    {
        await using var launched = await LaunchAsync("chromium");
        await using var context = await NewContextAsync(launched);
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Try it live" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".hero-prompt")).ToContainTextAsync("ldc.i4 6");
        await Assertions.Expect(page.Locator(".hero-prompt")).ToContainTextAsync("= 42 : int32");
        await Assertions.Expect(page.Locator(".install-hint code")).ToHaveTextAsync("dotnet tool install -g ilrepl");
    }

    // The session box is taller than Playwright's default viewport; clicks outside the viewport never land.
    // Only Chromium can grant clipboard access to a test.
    private static Task<IBrowserContext> NewContextAsync(IBrowser browser, bool clipboard = false) => browser.NewContextAsync(new BrowserNewContextOptions
    {
        ViewportSize = new ViewportSize { Width = 1280, Height = 1000 },
        Permissions = clipboard ? ["clipboard-read", "clipboard-write"] : null,
    });

    private static async Task<IPage> OpenSessionAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/try/");
        await WaitForSessionAsync(page, 1, 180_000);
        await ClickIntoTerminalAsync(page);
        return page;
    }

    private static async Task WaitForSessionAsync(IPage page, int count, float timeout)
    {
        await page.WaitForFunctionAsync($"() => window.ilreplReady === true && window.ilreplSessionCount >= {count}", null, new PageWaitForFunctionOptions { Timeout = timeout });
        await Assertions.Expect(page.Locator("#session-status")).ToHaveTextAsync("Ready");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("il[1]>", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    private static async Task ClickIntoTerminalAsync(IPage page)
    {
        var box = await page.Locator("#terminal").BoundingBoxAsync();
        Assert.IsNotNull(box);
        await page.Mouse.ClickAsync(box.X + (box.Width / 2), box.Y + (box.Height / 2));
    }

    private static async Task TypeLineAsync(IPage page, string line)
    {
        await page.Keyboard.TypeAsync(line);
        await page.Keyboard.PressAsync("Enter");
    }

    private static async Task ClearPromptAsync(IPage page)
    {
        await InputIdleAsync(page);
        await page.Keyboard.PressAsync("Control+c");
        await EmptyPromptAsync(page);
    }

    private static Task<IJSHandle> InputIdleAsync(IPage page) => page.WaitForFunctionAsync("""
        () => {
          const terminal = window.ilreplTerminal;
          const buffer = terminal.buffer.active;
          const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
          return !status.includes('updating') && !status.includes('sending') && !status.includes('cancelling')
            && !status.includes('Ctrl+C cancels');
        }
        """);

    private static async Task<IBrowser> LaunchAsync(string browser) => browser switch
    {
        "webkit" => await s_playwright!.Webkit.LaunchAsync(),
        _ => await s_playwright!.Chromium.LaunchAsync(),
    };

    private static async Task<string> BufferTextAsync(IPage page) => string.Join('\n', await BufferRowsAsync(page));

    private static Task<string[]> BufferRowsAsync(IPage page) => page.EvaluateAsync<string[]>(
        "() => Array.from({length: window.ilreplTerminal.rows}, (_, i) => (window.ilreplTerminal.buffer.active.getLine(i)?.translateToString(true) ?? ''))");

    /// <summary>
    /// A method defined in one cell is called from the next, in the browser.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_DefinesAndCallsMethod(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");

        await TypeLineAsync(page, ".method int32 Twice(int32 n) {");
        await Assertions.Expect(terminal).ToContainTextAsync("...>", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("Enter continues", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        Assert.DoesNotContain("\n  method int32 Twice(int32 n)", await BufferTextAsync(page), "nothing reaches the engine before the block closes");
        foreach (var line in new[] { "ldarg n", "ldc.i4 2", "mul", "ret", "}" })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("method int32 Twice(int32 n)", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Twice", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("il[2]>", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        await TypeLineAsync(page, "ldc.i4 21");
        await TypeLineAsync(page, "call int32 Twice(int32)");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 42 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("il[3]>", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// .dis reads bodies back in the browser: a session method, a filter, a class member, a calli,
    /// the framework, a generic definition, and an assembly loaded from the virtual file system whose
    /// vararg call site prints the optional argument types its reference carries.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task LiveSession_DisassemblesCorpus(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 60_000 };

        foreach (var line in new[] { ".method int32 Fib(int32 n) {", "ldarg n", "ldc.i4 2", "blt BASE", "ldarg n", "ldc.i4 1", "sub", "call int32 Fib(int32)", "ldarg n", "ldc.i4 2", "sub", "call int32 Fib(int32)", "add", "ret", "BASE: ldarg n", "ret", "}" })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("end of method Fib", options);
        await TypeLineAsync(page, ".dis Fib");
        await Assertions.Expect(terminal).ToContainTextAsync(".method public hidebysig static int32 Fib(int32 n) cil managed {", options);
        await Assertions.Expect(terminal).ToContainTextAsync("call int32 Fib(int32)", options);
        await Assertions.Expect(terminal).ToContainTextAsync("IL_002e:", options);

        foreach (var line in new[] { ".method int32 Safe(int32 d) {", ".locals init (int32 n)", ".try {", "ldc.i4 1", "ldarg d", "div", "stloc n", "leave END", "} filter {", "isinst DivideByZeroException", "ldnull", "cgt.un", "endfilter", "} handler {", "pop", "ldc.i4 42", "stloc n", "leave END", "}", "END: ldloc n", "ret", "}" })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("end of method Safe", options);
        await TypeLineAsync(page, ".dis Safe");
        await Assertions.Expect(terminal).ToContainTextAsync("} filter {", options);
        await Assertions.Expect(terminal).ToContainTextAsync("} handler {", options);
        await Assertions.Expect(terminal).ToContainTextAsync("endfilter", options);

        foreach (var line in new[] { ".class public Point {", ".field public int32 X", ".method public instance int32 Twice() {", "ldarg.0", "ldfld int32 Point::X", "ldc.i4 2", "mul", "ret", "}", "}" })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("end of class Point", options);
        await TypeLineAsync(page, ".dis instance int32 Point::Twice()");
        await Assertions.Expect(terminal).ToContainTextAsync("ldfld int32 Point::X", options);

        foreach (var line in new[] { ".method int32 Indirect() {", "ldc.i4 3", "ldc.i4 9", "ldftn int32 Math::Max(int32, int32)", "calli int32(int32, int32)", "ret", "}" })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("end of method Indirect", options);
        await TypeLineAsync(page, ".dis Indirect");
        await Assertions.Expect(terminal).ToContainTextAsync("calli int32(int32, int32)", options);

        // A framework body is long enough to scroll its header off the visible rows, so the tail is
        // what can be checked: in the browser it is read through reflection, and the note says so.
        await TypeLineAsync(page, ".dis instance string String::Trim()");
        await Assertions.Expect(terminal).ToContainTextAsync("the body was read through reflection", options);
        var trim = await BufferTextAsync(page);
        Assert.Contains("ret", trim);
        Assert.Contains("code size", trim);
        await TypeLineAsync(page, ".dis instance void class List`1<int32>::Add(!0)");
        await Assertions.Expect(terminal).ToContainTextAsync("showing the definition", options);
        await Assertions.Expect(terminal).ToContainTextAsync("AddWithResize(!0)", options);

        // An assembly from the virtual file system lists through its image; the vararg call site
        // prints the optional types from its own reference, with no fallback note.
        await TypeLineAsync(page, ".load /samples/Greeter.dll");
        await Assertions.Expect(terminal).ToContainTextAsync("loaded Greeter", options);
        await TypeLineAsync(page, ".dis int32 Greeter.Hello::CallCountArgs()");
        await Assertions.Expect(terminal).ToContainTextAsync("call vararg int32 [Greeter]Greeter.Hello::CountArgs(..., int32)", options);
        await Assertions.Expect(terminal).ToContainTextAsync("code size 8 (0x8)", options);
        var text = await BufferTextAsync(page);
        var greeter = text[text.IndexOf("CallCountArgs() cil managed", StringComparison.Ordinal)..];
        Assert.DoesNotContain("read through reflection", greeter, "the loaded image must serve the listing");
        Assert.DoesNotContain("could not be", greeter, "no operand may fall back");
        Assert.Contains("CountArgs(..., int32)", greeter);
    }

    /// <summary>
    /// A class declared in the browser is a type across cells: a struct instance is shown by its
    /// fields, a static keeps its value, and a member is called from a later cell.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task LiveSession_DefinesClassAndShowsFields(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");

        await TypeLineAsync(page, ".class public sequential ansi sealed Point extends [System.Runtime]System.ValueType {");
        await Assertions.Expect(terminal).ToContainTextAsync("Enter continues", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        foreach (var line in new[]
        {
            ".field public int32 X", ".field public int32 Y", ".field public static int32 Made",
            ".method public instance void .ctor(int32 x, int32 y) {", "ldarg.0", "ldarg x", "stfld int32 Point::X", "ldarg.0", "ldarg y", "stfld int32 Point::Y",
            "ldsfld int32 Point::Made", "ldc.i4 1", "add", "stsfld int32 Point::Made", "ret", "}",
            ".method public instance int32 Sum() {", "ldarg.0", "ldfld int32 Point::X", "ldarg.0", "ldfld int32 Point::Y", "add", "ret", "}",
            "}",
        })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("struct Point", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("end of struct Point", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("il[2]>", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        foreach (var line in new[] { "ldc.i4 3", "ldc.i4 4", "newobj instance void Point::.ctor(int32, int32)", "box Point", "ret" })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("= Point { X = 3, Y = 4 } : Point", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });

        foreach (var line in new[] { ".locals init (valuetype Point p)", "ldloca p", "ldc.i4 5", "ldc.i4 6", "call instance void Point::.ctor(int32, int32)", "ldloca p", "call instance int32 Point::Sum()", "ldsfld int32 Point::Made", "add", "ret" })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("= 13 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("il[4]>", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// The browser refuses incompatible branch stacks before the method is created and returns the block for correction.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_BadBranchInMethod_IsRejectedBeforeCreation(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");

        foreach (var line in new[] { ".method void Bad() {", "ldc.i4 0", "brfalse SKIP", "ldc.i4 1", "ldc.i4 2", "pop", "SKIP: pop", "}" })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("incompatible stacks",
            new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("editing 8 lines",
            new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        var text = await BufferTextAsync(page);
        Assert.DoesNotContain("end of method Bad", text, "the invalid definition was never committed");

        await ClearPromptAsync(page);
        await TypeLineAsync(page, "ldc.i4.s 42");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 42 : int32",
            new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// A block typed in the browser continues at Enter until its braces balance, then goes by
    /// line by line, and Up brings the whole block back as one entry.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_TypesBlockInBrowser(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };

        await TypeLineAsync(page, ".method int32 Twice(int32 n) {");
        await Assertions.Expect(terminal).ToContainTextAsync("editing 2 lines", options);
        await TypeLineAsync(page, "ldarg n");
        await TypeLineAsync(page, "ldc.i4 2");
        await TypeLineAsync(page, "mul");
        await TypeLineAsync(page, "ret");
        await page.Keyboard.TypeAsync("}");
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 6 lines", options);
        var rows = await BufferRowsAsync(page);
        Assert.Contains(r => r.TrimEnd() == "  ...>   ldarg n", rows, "the continuation rows carry the indentation:\n" + string.Join('\n', rows));
        Assert.Contains(r => r.TrimEnd() == "  ...> }", rows, "the close brace stepped out:\n" + string.Join('\n', rows));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Twice", options);
        await Assertions.Expect(terminal).ToContainTextAsync("il[2]>", options);

        await page.Keyboard.PressAsync("ArrowUp");
        await Assertions.Expect(terminal).ToContainTextAsync("editing 6 lines", options);
        rows = await BufferRowsAsync(page);
        Assert.Contains(r => r.TrimEnd() == "il[2]> .method int32 Twice(int32 n) {", rows, "the recalled block starts at the prompt:\n" + string.Join('\n', rows));
        await ClearPromptAsync(page);

        await TypeLineAsync(page, "ldc.i4 21");
        await TypeLineAsync(page, "call int32 Twice(int32)");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 42 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// A paste lands in the editor and waits for Enter; nothing runs until then.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_PasteWaitsForEnter(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };

        await PasteAsync(page, "ldc.i4 6\nldc.i4 7\nmul\nret\n");
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 4 lines", options);
        var text = await BufferTextAsync(page);
        Assert.DoesNotContain("┊ [int32]", text, "nothing runs on paste");
        Assert.Contains("...> ret", text, "the pasted lines sit in the editor");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("= 42 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// The copy button on the methods page yields text the session runs: pasted, it waits for
    /// Enter, then Fib is defined and returns 55.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task Docs_FibSource_CopiesPastesAndRuns(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var docs = await context.NewPageAsync();
        await docs.GotoAsync(s_site!.BaseUrl + "/usage/methods/");
        var source = await docs.Locator("pre code").First.InnerTextAsync();
        Assert.StartsWith(".method int32 Fib(int32 n) {", source.TrimStart(), "the first block on the page is the method's source");
        Assert.DoesNotContain("il[", source, "the source block carries no prompts");
        Assert.DoesNotContain("┊", source, "the source block carries no stack lines");

        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 60_000 };
        await PasteAsync(page, source.TrimEnd('\n') + "\n");
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 17 lines", options);
        Assert.DoesNotContain("method int32 Fib(int32 n)", await BufferTextAsync(page), "nothing reaches the engine before Enter");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Fib", options);
        await TypeLineAsync(page, "ldc.i4 10");
        await TypeLineAsync(page, "call int32 Fib(int32)");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 55 : int32", options);
    }

    /// <summary>
    /// History survives a quit: the next session in the same runtime recalls the block.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task LiveSession_HistorySurvivesQuitRestart(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };

        await TypeBlockAsync(page);
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Twice", options);
        await page.Keyboard.PressAsync("Control+q");
        await WaitForSessionAsync(page, 2, 60_000);
        await page.Keyboard.PressAsync("ArrowUp");
        await Assertions.Expect(terminal).ToContainTextAsync("editing 6 lines", options);
        Assert.Contains("il[1]> .method int32 Twice(int32 n) {", await BufferTextAsync(page));
        Assert.HasCount(1, await StoredHistoryAsync(page));
    }

    /// <summary>
    /// History survives the restart button, which starts a new worker.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task LiveSession_HistorySurvivesRestartButton(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };

        await TypeBlockAsync(page);
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Twice", options);
        await page.WaitForFunctionAsync("() => new Promise(r => { const q = indexedDB.open('ilrepl', 1); q.onsuccess = () => { const c = q.result.transaction('history').objectStore('history').count(); c.onsuccess = () => { q.result.close(); r(c.result === 1); }; }; q.onerror = () => r(false); })", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.Locator("#session-restart").ClickAsync();
        await WaitForSessionAsync(page, 2, 180_000);
        await ClickIntoTerminalAsync(page);
        await page.Keyboard.PressAsync("ArrowUp");
        await Assertions.Expect(terminal).ToContainTextAsync("editing 6 lines", options);
        Assert.Contains("il[1]> .method int32 Twice(int32 n) {", await BufferTextAsync(page));
    }

    /// <summary>
    /// History survives a reload of the page.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task LiveSession_HistorySurvivesReload(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };

        await TypeBlockAsync(page);
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Twice", options);
        await page.WaitForFunctionAsync("() => new Promise(r => { const q = indexedDB.open('ilrepl', 1); q.onsuccess = () => { const c = q.result.transaction('history').objectStore('history').count(); c.onsuccess = () => { q.result.close(); r(c.result === 1); }; }; q.onerror = () => r(false); })", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.ReloadAsync();
        await WaitForSessionAsync(page, 1, 180_000);
        await ClickIntoTerminalAsync(page);
        await page.Keyboard.PressAsync("ArrowUp");
        await Assertions.Expect(terminal).ToContainTextAsync("editing 6 lines", options);
        Assert.Contains("il[1]> .method int32 Twice(int32 n) {", await BufferTextAsync(page));
    }

    /// <summary>
    /// A first visit has no history and says nothing about it.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_FirstVisit_NoHistoryNoMessage(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");

        await page.Keyboard.PressAsync("ArrowUp");
        await TypeLineAsync(page, "ldc.i4 6");
        await Assertions.Expect(terminal).ToContainTextAsync("┊ [int32]", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        var text = await BufferTextAsync(page);
        Assert.DoesNotContain("history is not being saved", text);
        Assert.HasCount(1, await StoredHistoryAsync(page));
    }

    /// <summary>
    /// Two tabs that both loaded the same history and each append end with both entries.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(400_000, CooperativeCancellation = true)]
    public async Task LiveSession_TwoTabs_BothAppendsSurvive(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var first = await OpenSessionAsync(context);
        var second = await OpenSessionAsync(context);
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };

        await first.BringToFrontAsync();
        await ClickIntoTerminalAsync(first);
        await TypeBlockAsync(first);
        await Assertions.Expect(first.Locator("#terminal")).ToContainTextAsync("end of method Twice", options);
        await second.BringToFrontAsync();
        await ClickIntoTerminalAsync(second);
        await TypeLineAsync(second, "ldc.i4 6");
        await Assertions.Expect(second.Locator("#terminal")).ToContainTextAsync("┊ [int32]", options);

        var stored = await StoredHistoryAsync(second);
        Assert.HasCount(2, stored, "both tabs' entries are records");
        Assert.StartsWith(".method int32 Twice(int32 n) {", stored[0]);
        Assert.AreEqual("ldc.i4 6", stored[1]);

        foreach (var page in new[] { first, second })
        {
            await page.BringToFrontAsync();
            await page.ReloadAsync();
            await WaitForSessionAsync(page, 1, 180_000);
            await ClickIntoTerminalAsync(page);
            await page.Keyboard.PressAsync("ArrowUp");
            await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("il[1]> ldc.i4 6", options);
            await page.Keyboard.PressAsync("ArrowUp");
            await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("editing 6 lines", options);
        }
    }

    /// <summary>
    /// The store keeps the newest thousand entries: an append beyond that drops the oldest.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task LiveSession_History_PrunesBeyondThousand(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/");
        await page.EvaluateAsync(@"() => new Promise((resolve, reject) => {
            const q = indexedDB.open('ilrepl', 1);
            q.onupgradeneeded = () => q.result.createObjectStore('history', { autoIncrement: true });
            q.onsuccess = () => {
                const tx = q.result.transaction('history', 'readwrite');
                const store = tx.objectStore('history');
                for (let i = 0; i < 1000; i++) store.add('ldc.i4 ' + i);
                tx.oncomplete = () => { q.result.close(); resolve(); };
                tx.onerror = () => reject(tx.error);
            };
            q.onerror = () => reject(q.error);
        })");
        await page.GotoAsync(s_site.BaseUrl + "/try/");
        await WaitForSessionAsync(page, 1, 180_000);
        await ClickIntoTerminalAsync(page);
        await TypeLineAsync(page, "nop");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("1 instruction", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await page.WaitForFunctionAsync("() => new Promise(r => { const q = indexedDB.open('ilrepl', 1); q.onsuccess = () => { const c = q.result.transaction('history').objectStore('history').count(); c.onsuccess = () => { q.result.close(); r(c.result === 1000); }; }; q.onerror = () => r(false); })", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        var stored = await StoredHistoryAsync(page);
        Assert.HasCount(1000, stored);
        Assert.AreEqual("ldc.i4 1", stored[0], "the oldest entry is gone");
        Assert.AreEqual("nop", stored[^1], "the new entry is the newest");
    }

    /// <summary>
    /// When the database is not available the session says so once and goes on.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_StorageUnavailable_PrintsLine(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        // The worker's interop module is served with the database taken away ahead of it, so the
        // real load path is what rejects.
        await context.RouteAsync("**/try/interop.js", async route =>
        {
            var response = await route.FetchAsync();
            var body = await response.TextAsync();
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Response = response,
                Body = "const indexedDB = undefined;\n" + body,
                ContentType = "text/javascript",
            });
        });
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };

        await Assertions.Expect(terminal).ToContainTextAsync("history is not being saved: IndexedDB is not available", options);
        await TypeLineAsync(page, "ldc.i4 6");
        await Assertions.Expect(terminal).ToContainTextAsync("┊ [int32]", options);
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 6 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        var text = await BufferTextAsync(page);
        Assert.AreEqual(1, text.Split("history is not being saved").Length - 1, "the problem is reported once");
        await page.Keyboard.PressAsync("ArrowUp");
        await Assertions.Expect(terminal).ToContainTextAsync("il[2]> ret", options);
    }

    /// <summary>
    /// A long block shows progress and repaints the status bar at the new width after a viewport change.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(400_000, CooperativeCancellation = true)]
    public async Task LiveSession_WhileSending_ShowsProgressAndRepaints(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 60_000 };

        await PasteAsync(page, LongMethod(3002));
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 3002 lines", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("sending", options);
        await Assertions.Expect(terminal).ToContainTextAsync("Ctrl+C cancels", options);
        var before = await page.EvaluateAsync<int>("() => window.ilreplTerminal.cols");
        await page.SetViewportSizeAsync(900, 1000);
        await page.WaitForFunctionAsync("""
            before => {
              const terminal = window.ilreplTerminal;
              const buffer = terminal.buffer.active;
              return terminal.cols < before
                && buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true).includes('sending');
            }
            """, before, new() { PollingInterval = 16, Timeout = 30_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Long", new LocatorAssertionsToContainTextOptions { Timeout = 180_000 });
        await Assertions.Expect(terminal).Not.ToContainTextAsync("sending", options);
    }

    /// <summary>
    /// Ctrl+C during a long block stops it, withdraws it, and leaves its text in the editor.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(400_000, CooperativeCancellation = true)]
    public async Task LiveSession_WhileSending_CtrlCStopsWithRemainderInEditor(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 60_000 };

        await PasteAsync(page, LongMethod(3002));
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 3002 lines", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync(StartedLongSubmission(), options);
        await page.Keyboard.PressAsync("Control+c");
        await Assertions.Expect(terminal).ToContainTextAsync("method Long abandoned; the block is back in the editor", options);
        await Assertions.Expect(terminal).ToContainTextAsync("editing 3002 lines", options);
        Assert.DoesNotContain("end of method Long", await BufferTextAsync(page));
        await ClearPromptAsync(page);
        await TypeLineAsync(page, "ldc.i4 6");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 6 : int32", options);
    }

    /// <summary>
    /// Enter during a long block runs nothing of its own: the block cannot go twice, and the
    /// blank line waits its turn.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(400_000, CooperativeCancellation = true)]
    public async Task LiveSession_WhileSending_ExtraEnterRunsNothing(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 60_000 };

        await PasteAsync(page, LongMethod(3002));
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 3002 lines", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("sending", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Long", new LocatorAssertionsToContainTextOptions { Timeout = 180_000 });
        await Assertions.Expect(terminal).Not.ToContainTextAsync("sending", options);
        var text = await BufferTextAsync(page);
        Assert.AreEqual(1, text.Split("end of method Long").Length - 1, "the block went once");
        Assert.DoesNotContain("error", text);
        await TypeLineAsync(page, "ldc.i4 6");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 6 : int32", options);
    }

    /// <summary>
    /// A sixty-line method pasted into the browser goes by in well under ten seconds.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task LiveSession_Pastes60LineMethod_CompletesWithinTenSeconds(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");

        await PasteAsync(page, LongMethod(60));
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 60 lines", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Long", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        watch.Stop();
        TestContext.WriteLine($"60 lines in {browser} in {watch.Elapsed.TotalSeconds:F2} s");
        Assert.IsLessThan(TimeSpan.FromSeconds(10), watch.Elapsed, $"took {watch.Elapsed}");
    }

    // A method of exactly this many lines: the header, nops, ret, and the close.
    /// <summary>
    /// An entry that holds a NUL character comes back as one entry: the store's entries cross
    /// to the session as JSON, not joined on a sentinel.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task LiveSession_History_EntryWithNul_StaysOneEntry(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/");
        await page.EvaluateAsync(@"() => new Promise((resolve, reject) => {
            const q = indexedDB.open('ilrepl', 1);
            q.onupgradeneeded = () => q.result.createObjectStore('history', { autoIncrement: true });
            q.onsuccess = () => {
                const tx = q.result.transaction('history', 'readwrite');
                const store = tx.objectStore('history');
                store.add('nop');
                store.add('a\u0000b');
                tx.oncomplete = () => { q.result.close(); resolve(); };
                tx.onerror = () => reject(tx.error);
            };
            q.onerror = () => reject(q.error);
        })");
        await page.GotoAsync(s_site.BaseUrl + "/try/");
        await WaitForSessionAsync(page, 1, 180_000);
        await ClickIntoTerminalAsync(page);
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await page.Keyboard.PressAsync("ArrowUp");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("il[1]> a", options);
        await page.Keyboard.PressAsync("ArrowUp");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("il[1]> nop", options);
        await page.Keyboard.PressAsync("ArrowUp");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("il[1]> nop", options);
        var rows = await BufferRowsAsync(page);
        Assert.Contains(r => r.TrimEnd() == "il[1]> nop", rows, "the oldest entry is nop, so the NUL entry was one entry, not two:\n" + string.Join('\n', rows));
    }

    private static string LongMethod(int lines) => ".method void Long() {\n" + string.Concat(Enumerable.Repeat("  nop\n", lines - 3)) + "  ret\n}\n";

    private static async Task TypeBlockAsync(IPage page)
    {
        foreach (var line in new[] { ".method int32 Twice(int32 n) {", "ldarg n", "ldc.i4 2", "mul", "ret", "}" })
        {
            await TypeLineAsync(page, line);
        }
    }

    // A paste reaches xterm the way the browser delivers one: as a paste event on its textarea,
    // which xterm wraps in the bracketed paste markers the app asked for.
    private static async Task PasteAsync(IPage page, string text)
    {
        await page.EvaluateAsync(
            "text => { const t = document.querySelector('.xterm-helper-textarea'); const dt = new DataTransfer(); dt.setData('text/plain', text); t.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true })); }",
            text);
    }

    private static Task<string[]> StoredHistoryAsync(IPage page) => page.EvaluateAsync<string[]>(
        "() => new Promise((resolve, reject) => { const q = indexedDB.open('ilrepl', 1); q.onupgradeneeded = () => q.result.createObjectStore('history', { autoIncrement: true }); q.onsuccess = () => { const r = q.result.transaction('history').objectStore('history').getAll(); r.onsuccess = () => { q.result.close(); resolve(r.result); }; r.onerror = () => reject(r.error); }; q.onerror = () => reject(q.error); })");
}
