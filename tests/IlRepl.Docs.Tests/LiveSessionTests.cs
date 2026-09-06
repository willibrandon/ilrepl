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
        await using var context = await launched.NewContextAsync();
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
        await using var context = await launched.NewContextAsync();
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
        await using var context = await launched.NewContextAsync();
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
        await using var context = await launched.NewContextAsync();
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
        await using var context = await launched.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Try it live" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".hero-prompt")).ToContainTextAsync("ldc.i4 6");
        await Assertions.Expect(page.Locator(".hero-prompt")).ToContainTextAsync("= 42 : int32");
        await Assertions.Expect(page.Locator(".install-hint code")).ToHaveTextAsync("dotnet tool install -g ilrepl");
    }

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
