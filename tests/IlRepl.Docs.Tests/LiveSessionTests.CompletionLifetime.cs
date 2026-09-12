using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    [System.Text.RegularExpressions.GeneratedRegex("retained=(-?[0-9]+)")]
    private static partial System.Text.RegularExpressions.Regex PreviewRetainedBytes();

    /// <summary>
    /// Qualified facade and generic previews resolve on Mono without loading assemblies or invoking resolution handlers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_RawMetadata_ResolvesWithoutCallbacks(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        page.Console += (_, message) => TestContext.WriteLine(message.Text);
        await TypeLineAsync(page, ".load /samples/Greeter.dll");
        await ExpectCompletionAsync(page, "loaded Greeter");
        await TypeLineAsync(page, "call [Greeter]Greeter.CompletionProbe::Begin()");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "il[2]>");
        await page.Keyboard.TypeAsync("ldtoken [System.Runtime]System.Strin");
        await ExpectCompletionAsync(page, "❯ string");
        await ClearPromptAsync(page);
        await page.Keyboard.TypeAsync("ldtoken [System.Collections]System.Collections.Generic.List");
        await ExpectCompletionAsync(page, "❯ List<");
        await ClearPromptAsync(page);
        await TypeLineAsync(page, ".clear");
        await TypeLineAsync(page, "call [System.Runtime]System.Reflection.Assembly::GetExecutingAssembly()");
        await TypeLineAsync(page, "call [Greeter]Greeter.CompletionProbe::Report([System.Runtime]System.Reflection.Assembly)");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "preview loads=0; modules=0; callbacks=0;");
        await TypeLineAsync(page, ".clear");
        await page.Keyboard.TypeAsync("ldtoken [System.Runtime]System.Strin");
        await ExpectCompletionAsync(page, "❯ string");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= typeof(string)");
        await TypeLineAsync(page, ".clear");
        await page.Keyboard.TypeAsync("ldtoken [System.Collections]System.Collections.Generic.List");
        await ExpectCompletionAsync(page, "❯ List<");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("int32");
        await ExpectCompletionAsync(page, "❯ int32");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "List<int32>");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Repeated generic previews remain available after a thousand edits and a same-worker session restart.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DoNotParallelize]
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
        await TypeLineAsync(page, ".load /samples/Greeter.dll");
        await ExpectCompletionAsync(page, "loaded Greeter");
        await TypeLineAsync(page, "call [Greeter]Greeter.CompletionProbe::Begin()");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "il[2]>");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var lastSource = "";
        for (var edit = 0; edit < 1000; edit++)
        {
            TestContext.CancellationToken.ThrowIfCancellationRequested();
            if (edit != 0)
            {
                await ClearPromptAsync(page);
            }

            var name = "PreviewBox" + edit;
            lastSource = $".class public {name}<T> {{\n.field public !0 value\n"
                + ".class nested public Inner<U> { }\n}\n.typeparams (TPreview)\n";
            await PasteAsync(page, lastSource + $"ldtoken {name}");
            await page.WaitForFunctionAsync("name => document.querySelector('#terminal').textContent.includes('❯ ' + name + '<')",
                name, new() { PollingInterval = 16, Timeout = 30_000 });
        }

        TestContext.WriteLine($"1000 generic editor previews in {browser}: {started.Elapsed.TotalSeconds:F1} s");
        await ClearPromptAsync(page);
        await TypeLineAsync(page, ".clear");
        await TypeLineAsync(page, "call [System.Runtime]System.Reflection.Assembly::GetExecutingAssembly()");
        await TypeLineAsync(page, "call [Greeter]Greeter.CompletionProbe::Report([System.Runtime]System.Reflection.Assembly)");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "preview loads=0; modules=0; callbacks=0;");
        var measured = await BufferTextAsync(page);
        var retained = PreviewRetainedBytes().Match(measured);
        Assert.IsTrue(retained.Success, measured);
        var bytes = long.Parse(retained.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsLessThan(20_000_000L, bytes, "Preview history must remain bounded after one thousand browser edits.");
        TestContext.WriteLine($"Browser preview retained bytes in {browser}: {bytes:N0}");
        await TypeLineAsync(page, ".clear");
        await PasteAsync(page, lastSource + "ldtoken PreviewBox999");
        await ExpectCompletionAsync(page, "❯ PreviewBox999<");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("str");
        await Assertions.Expect(terminal).ToContainTextAsync("❯ string", options);
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of class PreviewBox999", options);
        await TypeLineAsync(page, ".clear");
        await TypeLineAsync(page, ".typeargs (int32)");
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
        await CompletionAtCaretAsync(page, "il[2]> call CompletionLon", "PgUp/PgDn");
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
