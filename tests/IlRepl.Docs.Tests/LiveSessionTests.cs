using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Opens the built docs site in a real browser and drives the live session, which runs the
/// engine and the Hex1b UI on the .NET WebAssembly runtime.
/// </summary>
[TestClass]
public sealed class LiveSessionTests
{
    private static StaticSite? s_site;
    private static IPlaywright? s_playwright;

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
    /// Dragging across the transcript selects text in the app, Enter copies it, and the page puts
    /// it on the clipboard. The prompt works again afterwards, the wheel scrolls the transcript,
    /// and the caret is a blinking block.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task Drag_CopiesSelection_AndWheelScrolls(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched, clipboard: browser == "chromium");
        var page = await OpenSessionAsync(context);
        var cursor = await page.EvaluateAsync<string[]>("() => [window.ilreplTerminal.options.cursorStyle, String(window.ilreplTerminal.options.cursorBlink)]");
        Assert.AreEqual("block", cursor[0], "the caret should be a block");
        Assert.AreEqual("true", cursor[1], "the caret should blink");

        await TypeLineAsync(page, "ldc.i4 6");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("┊ [int32]", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        // Drag across the echoed line on the second row, then y yanks it and the status bar says so.
        var screen = await page.Locator(".xterm-screen").BoundingBoxAsync();
        Assert.IsNotNull(screen);
        var size = await page.EvaluateAsync<int[]>("() => [window.ilreplTerminal.cols, window.ilreplTerminal.rows]");
        var cellWidth = screen.Width / size[0];
        var cellHeight = screen.Height / size[1];
        var y = screen.Y + (cellHeight * 1.5f);
        await page.Mouse.MoveAsync(screen.X + (cellWidth * 0.5f), y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(screen.X + (cellWidth * 14.5f), y, new MouseMoveOptions { Steps = 8 });
        await page.Mouse.UpAsync();
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("y yank", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        await page.Keyboard.PressAsync("y");
        await page.WaitForFunctionAsync("() => typeof window.ilreplLastCopy === 'string'", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        var copied = await page.EvaluateAsync<string>("() => window.ilreplLastCopy");
        Assert.Contains("ldc.i4 6", copied, "the selected row should be what was copied");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("Yanked: il[1]> ldc.i4 6", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("y yank", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        if (browser == "chromium")
        {
            Assert.Contains("ldc.i4 6", await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"), "the copy should be on the clipboard");
        }

        // Shift+Up selects from the keyboard: the last line, then the one above; y yanks both.
        await page.EvaluateAsync("() => { window.ilreplLastCopy = null; }");
        await page.Keyboard.PressAsync("Shift+ArrowUp");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("y yank", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        await page.Keyboard.PressAsync("Shift+ArrowUp");
        await page.Keyboard.PressAsync("y");
        await page.WaitForFunctionAsync("() => typeof window.ilreplLastCopy === 'string'", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        var yanked = await page.EvaluateAsync<string>("() => window.ilreplLastCopy");
        Assert.Contains("ldc.i4 6", yanked, "the echoed line should be in the yank");
        Assert.Contains("[int32]", yanked, "the stack line should be in the yank");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("Yanked 2 lines", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

        // Copy mode has ended; a click and typing go to the prompt.
        await ClickIntoTerminalAsync(page);
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 6 : int32", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });

        // The help is taller than the box; the wheel scrolls the transcript up and back down.
        await TypeLineAsync(page, ".help");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("Ctrl+Q leaves.", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await page.Mouse.MoveAsync(screen.X + (screen.Width / 2), screen.Y + (screen.Height / 3));
        await page.Mouse.WheelAsync(0, -1000);
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("Ctrl+Q leaves.", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        await page.Mouse.WheelAsync(0, 1000);
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("Ctrl+Q leaves.", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

        // The scrollbar thumb in the last column can be dragged up as well.
        var thumbX = screen.X + (cellWidth * (size[0] - 0.5f));
        await page.Mouse.MoveAsync(thumbX, screen.Y + (cellHeight * (size[1] - 5.5f)));
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(thumbX, screen.Y + (cellHeight * 1.5f), new MouseMoveOptions { Steps = 10 });
        await page.Mouse.UpAsync();
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("Ctrl+Q leaves.", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("Type one IL instruction", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
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

    private static async Task<IBrowser> LaunchAsync(string browser) => browser switch
    {
        "webkit" => await s_playwright!.Webkit.LaunchAsync(),
        _ => await s_playwright!.Chromium.LaunchAsync(),
    };

    private static async Task<string> BufferTextAsync(IPage page) => string.Join('\n', await BufferRowsAsync(page));

    private static Task<string[]> BufferRowsAsync(IPage page) => page.EvaluateAsync<string[]>(
        "() => Array.from({length: window.ilreplTerminal.rows}, (_, i) => (window.ilreplTerminal.buffer.active.getLine(i)?.translateToString(true) ?? ''))");
}
