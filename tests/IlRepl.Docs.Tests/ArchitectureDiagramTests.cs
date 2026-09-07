using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Opens the built docs site in a real browser and drives the diagrams on the architecture page:
/// pointing at a part, tabbing to it, and tapping it all explain it in the caption.
/// </summary>
[TestClass]
public sealed class ArchitectureDiagramTests
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
        if (!Directory.Exists(SitePaths.Dist))
        {
            Assert.Inconclusive("build the docs first: pnpm --dir docs build");
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
    /// Pointing at a part shows its explanation, leaving restores the hint, and tabbing works the same way.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Diagram_HoverAndFocus_ExplainTheParts(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await launched.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 1000 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/reference/architecture/");

        var caption = page.Locator("#process-diagram .il-diagram-caption");
        await Assertions.Expect(caption).ToContainTextAsync("Point at a part of the diagram");

        await page.Locator("#process-diagram [data-part='stack']").HoverAsync();
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("stack simulation");
        await Assertions.Expect(caption).ToContainTextAsync("before it is accepted");

        await page.Locator("#process-diagram [data-part='channel']").HoverAsync();
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("JSON-RPC over stdio");
        await Assertions.Expect(page.Locator("#process-diagram")).ToHaveAttributeAsync("data-active", "channel");

        await page.Mouse.MoveAsync(5, 5);
        await Assertions.Expect(caption).ToContainTextAsync("Point at a part of the diagram");

        // Keyboard: focus lands on a part and explains it; Tab moves to the next one.
        await page.Locator("#process-diagram [data-part='cli']").FocusAsync();
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("command line");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("terminal UI");

        var cell = page.Locator("#cell-diagram .il-diagram-caption");
        await page.Locator("#cell-diagram [data-part='replay']").HoverAsync();
        await Assertions.Expect(cell.Locator(".il-diagram-caption-title")).ToHaveTextAsync("replay");
        await Assertions.Expect(cell).ToContainTextAsync("MethodBuilder");
    }

    /// <summary>
    /// A tap pins a part so its explanation stays after the pointer leaves, and a second tap releases it.
    /// </summary>
    /// <param name="browser">The browser engine to drive.</param>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Diagram_Click_PinsThePart(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await launched.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 1000 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(s_site!.BaseUrl + "/reference/architecture/");

        var caption = page.Locator("#process-diagram .il-diagram-caption");
        var session = page.Locator("#process-diagram [data-part='session']");
        await session.ClickAsync();
        await page.Mouse.MoveAsync(5, 5);
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("the session");
        await Assertions.Expect(session).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("is-active"));

        await session.ClickAsync();
        await page.Mouse.MoveAsync(5, 5);
        await Assertions.Expect(caption).ToContainTextAsync("Point at a part of the diagram");
    }

    private static async Task<IBrowser> LaunchAsync(string browser) => browser switch
    {
        "webkit" => await s_playwright!.Webkit.LaunchAsync(),
        _ => await s_playwright!.Chromium.LaunchAsync(),
    };
}
