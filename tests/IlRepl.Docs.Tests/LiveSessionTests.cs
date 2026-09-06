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

        await page.EvaluateAsync("() => window.ilreplTerminal.focus()");
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
        await Assertions.Expect(page.Locator(".install-hint code")).ToHaveTextAsync("dotnet tool install -g ilrepl");
    }

    private static async Task<IBrowser> LaunchAsync(string browser) => browser switch
    {
        "webkit" => await s_playwright!.Webkit.LaunchAsync(),
        _ => await s_playwright!.Chromium.LaunchAsync(),
    };

    private static async Task<string> BufferTextAsync(IPage page)
    {
        var lines = await page.EvaluateAsync<string[]>(
            "() => Array.from({length: window.ilreplTerminal.rows}, (_, i) => (window.ilreplTerminal.buffer.active.getLine(i)?.translateToString(true) ?? ''))");
        return string.Join('\n', lines);
    }
}
