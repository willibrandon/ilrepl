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
        await Assertions.Expect(terminal).ToContainTextAsync("method int32 Twice(int32 n)", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("method Twice", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        foreach (var line in new[] { "ldarg n", "ldc.i4 2", "mul", "ret", "}" })
        {
            await TypeLineAsync(page, line);
        }

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
        await Assertions.Expect(terminal).ToContainTextAsync("struct Point", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
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
    /// The browser runtime cannot prepare a method ahead of a call, so a body the JIT would refuse
    /// closes without complaint there and is rejected at the first call instead.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_BadBranchInMethod_IsRejectedAtFirstCall(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");

        foreach (var line in new[] { ".method void Bad() {", "ldc.i4 0", "brfalse SKIP", "ldc.i4 1", "ldc.i4 2", "pop", "SKIP: pop", "}" })
        {
            await TypeLineAsync(page, line);
        }

        await Assertions.Expect(terminal).ToContainTextAsync("end of method Bad", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(terminal).ToContainTextAsync("il[2]>", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        var text = await BufferTextAsync(page);
        Assert.DoesNotContain("rejected method Bad", text, "the browser skips preparation at the close");

        await TypeLineAsync(page, "call void Bad()");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("error: the JIT rejected the cell", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
    }
}
