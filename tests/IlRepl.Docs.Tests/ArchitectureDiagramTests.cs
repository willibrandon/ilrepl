using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Opens the built docs site in a real browser and drives the diagrams on the architecture page:
/// pointing at a part, tabbing to it, and tapping it all explain it in the caption.
/// </summary>
[TestClass]
public sealed partial class ArchitectureDiagramTests
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
        var processText = page.Locator("#process-diagram").Locator("xpath=following-sibling::p[1]");
        await Assertions.Expect(caption).ToContainTextAsync("Point at a part of the diagram");
        var processTextTop = await TopAsync(processText);

        await page.Locator("#process-diagram [data-part='stack']").HoverAsync();
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("stack simulation");
        await Assertions.Expect(caption).ToContainTextAsync("before it is accepted");
        Assert.AreEqual(processTextTop, await TopAsync(processText), 0.5, "Hovering must not move the text after the process diagram.");

        await page.Locator("#process-diagram [data-part='channel']").HoverAsync();
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("JSON-RPC over stdio");
        await Assertions.Expect(page.Locator("#process-diagram")).ToHaveAttributeAsync("data-active", "channel");
        Assert.AreEqual(processTextTop, await TopAsync(processText), 0.5, "Long explanations must not move the following text.");

        await page.Mouse.MoveAsync(5, 5);
        await Assertions.Expect(caption).ToContainTextAsync("Point at a part of the diagram");
        Assert.AreEqual(processTextTop, await TopAsync(processText), 0.5, "Leaving must not move the text after the process diagram.");

        // Keyboard: focus lands on a part and explains it; Tab moves to the next one.
        await page.Locator("#process-diagram [data-part='cli']").FocusAsync();
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("command line");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("terminal UI");

        var cell = page.Locator("#cell-diagram .il-diagram-caption");
        var cellText = page.Locator("#cell-diagram").Locator("xpath=following-sibling::p[1]");
        var cellTextTop = await TopAsync(cellText);
        await page.Locator("#cell-diagram [data-part='replay']").HoverAsync();
        await Assertions.Expect(cell.Locator(".il-diagram-caption-title")).ToHaveTextAsync("replay");
        await Assertions.Expect(cell).ToContainTextAsync("MethodBuilder");
        Assert.AreEqual(cellTextTop, await TopAsync(cellText), 0.5, "Hovering must not move the text after the cell diagram.");
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
        await Assertions.Expect(session).ToHaveClassAsync(Active());

        await session.ClickAsync();
        await page.Mouse.MoveAsync(5, 5);
        await Assertions.Expect(caption).ToContainTextAsync("Point at a part of the diagram");

        await page.SetViewportSizeAsync(390, 844);
        var processText = page.Locator("#process-diagram").Locator("xpath=following-sibling::p[1]");
        var processTextTop = await TopAsync(processText);
        await session.ClickAsync();
        await Assertions.Expect(caption.Locator(".il-diagram-caption-title")).ToHaveTextAsync("the session");
        Assert.AreEqual(processTextTop, await TopAsync(processText), 0.5, "Tapping must not move the text on a narrow screen.");
    }

    private static async Task<IBrowser> LaunchAsync(string browser) => browser switch
    {
        "webkit" => await s_playwright!.Webkit.LaunchAsync(),
        _ => await s_playwright!.Chromium.LaunchAsync(),
    };

    private static Task<double> TopAsync(ILocator locator) =>
        locator.EvaluateAsync<double>("element => element.getBoundingClientRect().top + window.scrollY");

    // The class a part of the diagram wears while it is the one described.
    [GeneratedRegex("is-active")]
    private static partial Regex Active();
}
