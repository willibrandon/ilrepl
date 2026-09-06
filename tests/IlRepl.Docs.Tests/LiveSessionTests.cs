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
    private static IBrowser? s_browser;

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
        s_browser = await s_playwright.Chromium.LaunchAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the browser and the server.
    /// </summary>
    /// <returns>A task that completes when both are gone.</returns>
    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (s_browser is not null)
        {
            await s_browser.DisposeAsync().ConfigureAwait(false);
        }

        s_playwright?.Dispose();
        if (s_site is not null)
        {
            await s_site.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The runtime boots, the UI renders, and a cell compiles and runs in the browser.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task LiveSession_RunsCellInBrowser()
    {
        await using var context = await s_browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/try/");
        await page.WaitForFunctionAsync("() => window.ilreplReady === true", null, new PageWaitForFunctionOptions { Timeout = 120_000 });
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
    /// The home page links to the live session and shows the recorded demo.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Home_LinksToLiveSessionAndShowsDemo()
    {
        await using var context = await s_browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Try it in the browser" })).ToBeVisibleAsync();
        var demo = page.Locator("img.ilrepl-demo");
        await Assertions.Expect(demo).ToBeVisibleAsync();
        var naturalWidth = await demo.EvaluateAsync<int>("img => img.naturalWidth");
        Assert.IsGreaterThan(0, naturalWidth, "the demo image should load");
    }

    private static async Task<string> BufferTextAsync(IPage page)
    {
        var lines = await page.EvaluateAsync<string[]>(
            "() => Array.from({length: window.ilreplTerminal.rows}, (_, i) => (window.ilreplTerminal.buffer.active.getLine(i)?.translateToString(true) ?? ''))");
        return string.Join('\n', lines);
    }
}
