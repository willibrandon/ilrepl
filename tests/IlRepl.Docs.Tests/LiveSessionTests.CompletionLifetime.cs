using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Repeated generic previews remain available after a thousand edits and a same-worker session restart.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task LiveSession_ThousandGenericEdits_PreserveCompletionAndRestart(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var edit = 0; edit < 1000; edit++)
        {
            TestContext.CancellationToken.ThrowIfCancellationRequested();
            if (edit != 0)
            {
                await page.Keyboard.PressAsync("Control+c");
            }

            var name = "PreviewBox" + edit;
            await PasteAsync(page, $".class public {name}<T> {{\n.field public !0 value\n}}\nldtoken {name}");
            await Assertions.Expect(terminal).ToContainTextAsync("❯ " + name + "<", options);
        }

        TestContext.WriteLine($"1000 generic editor previews in {browser}: {started.Elapsed.TotalSeconds:F1} s");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("str");
        await Assertions.Expect(terminal).ToContainTextAsync("❯ string", options);
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of class PreviewBox999", options);
        await TypeLineAsync(page, ".clear");
        await TypeLineAsync(page, "ldtoken PreviewBox999<string>");
        await TypeLineAsync(page, "call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("PreviewBox999", options);
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
        await page.Keyboard.PressAsync("Control+q");
        await page.WaitForFunctionAsync("() => window.ilreplLastRestart === 'quit'");
        await Assertions.Expect(terminal).ToContainTextAsync("il[1]>", options);
        await page.Keyboard.TypeAsync("call Array::Empt");
        await Assertions.Expect(terminal).ToContainTextAsync("members 1/1", options);
        Assert.DoesNotContain("PreviewBox999", await BufferTextAsync(page));
    }

    /// <summary>
    /// A real method's complete signature can be scrolled to its final parameter in both browsers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_LongSignature_RevealsTheLastParameter(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        var parameters = string.Join(", ", Enumerable.Repeat("string", 240).Append("float64"));
        await PasteAsync(page, ".method void CompletionLong(" + parameters + ") {\nret\n}");
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 3 lines", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of method CompletionLong", options);
        await page.Keyboard.TypeAsync("call CompletionLon");
        await Assertions.Expect(terminal).ToContainTextAsync("PgUp/PgDn", options);
        var initialRows = await BufferRowsAsync(page);
        var title = initialRows.First(row => row.TrimStart().StartsWith("│detail", StringComparison.Ordinal));
        var pages = int.Parse(title.Split('/')[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        for (var line = 2; line <= pages; line++)
        {
            await page.Keyboard.PressAsync("PageDown");
            await Assertions.Expect(terminal).ToContainTextAsync($"detail {line}/{pages}", options);
        }

        var rows = await BufferRowsAsync(page);
        var detail = Array.FindIndex(rows, row => row.TrimStart().StartsWith("│detail", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, detail);
        Assert.Contains("float64)", string.Join("\n", rows.Skip(detail)));
        Assert.Contains("call CompletionLon", rows[^2]);
    }
}
