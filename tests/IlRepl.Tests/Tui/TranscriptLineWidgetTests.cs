using System.Collections.Concurrent;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Theming;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Exercises retained styled transcript trees through real rendering, parent themes, wrapping, and record-copy changes.
/// </summary>
[TestClass]
public sealed class TranscriptLineWidgetTests
{
    /// <summary>
    /// Supplies cancellation to the independently running terminal.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reused rows inherit the current parent theme without leaking styles, while copied rows refresh width, flash, and source.
    /// </summary>
    /// <param name="cache">Whether the terminal reuses cached node surfaces.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetainedTree_TracksThemeAndRecordCopiesWithoutChangingSiblings(bool cache)
    {
        var changes = new ConcurrentQueue<Action>();
        var widget = new TranscriptLineWidget(new TranscriptLine(LineKind.Output,
            [new("plain "), new("red", SpanStyle.Error), new(" 界", SpanStyle.String)]), 40).CacheRendering();
        var neighbor = new TranscriptLineWidget(TranscriptLine.Of(LineKind.Output, "neighbor"), 40).CacheRendering();
        var foreground = Hex1bColor.FromRgb(180, 190, 200);
        var background = Hex1bColor.FromRgb(12, 24, 36);
        var phase = 0;
        Hex1bApp app = null!;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithHeadless().WithDimensions(40, 10)
            .WithHex1bApp(options => options.EnableRenderCaching = cache, instance =>
            {
                app = instance;
                return ctx =>
                {
                    while (changes.TryDequeue(out var change)) change();
                    return ctx.ThemePanel(theme => theme.Set(GlobalTheme.ForegroundColor, foreground)
                        .Set(GlobalTheme.BackgroundColor, background),
                        ctx.VStack(v => [widget, neighbor, v.Text("phase " + phase)]));
                };
            }).Build();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var run = terminal.RunAsync(cancellation.Token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));
        try
        {
            await auto.WaitUntilTextAsync("phase 0");
            using (var snapshot = auto.CreateSnapshot())
            {
                AssertCell(snapshot, "plain", foreground, background);
                AssertCell(snapshot, "red", SpanPalette.Color(SpanStyle.Error), background);
                AssertCell(snapshot, "界", SpanPalette.Color(SpanStyle.String), background);
                AssertCell(snapshot, "neighbor", foreground, background);
            }

            await ChangeAsync(() =>
            {
                foreground = Hex1bColor.FromRgb(210, 180, 160);
                background = Hex1bColor.FromRgb(24, 36, 48);
            });
            using (var snapshot = auto.CreateSnapshot())
            {
                AssertCell(snapshot, "plain", foreground, background);
                AssertCell(snapshot, "red", SpanPalette.Color(SpanStyle.Error), background);
                AssertCell(snapshot, "neighbor", foreground, background);
            }

            await ChangeAsync(() => widget = widget with { Width = 6 });
            using (var snapshot = auto.CreateSnapshot())
            {
                Assert.AreEqual("plain", AppTest.Row(snapshot, 0));
                Assert.AreEqual("red 界", AppTest.Row(snapshot, 1));
                Assert.AreEqual("neighbor", AppTest.Row(snapshot, 2));
                AssertCell(snapshot, "red", SpanPalette.Color(SpanStyle.Error), background);
                AssertCell(snapshot, "界", SpanPalette.Color(SpanStyle.String), background);
            }

            await ChangeAsync(() => widget = widget with { Flash = true });
            using (var snapshot = auto.CreateSnapshot())
            {
                AssertCell(snapshot, "red", SpanPalette.Color(SpanStyle.Error), Hex1bColor.FromRgb(46, 92, 60));
                AssertCell(snapshot, "neighbor", foreground, background);
            }
            await ChangeAsync(() => widget = widget with { Flash = false, Width = 40 });
            using (var snapshot = auto.CreateSnapshot())
            {
                Assert.AreEqual("plain red 界", AppTest.Row(snapshot, 0));
                AssertCell(snapshot, "red", SpanPalette.Color(SpanStyle.Error), background);
            }

            await ChangeAsync(() => widget = widget with
            {
                Line = TranscriptLine.Of(LineKind.Output, "replacement", SpanStyle.Number),
            });
            using (var snapshot = auto.CreateSnapshot())
            {
                Assert.IsFalse(snapshot.ContainsText("plain"));
                AssertCell(snapshot, "replacement", SpanPalette.Color(SpanStyle.Number), background);
                AssertCell(snapshot, "neighbor", foreground, background);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        async Task ChangeAsync(Action change)
        {
            var expected = phase + 1;
            changes.Enqueue(() => { change(); phase = expected; });
            app.Invalidate();
            await auto.WaitUntilTextAsync("phase " + expected);
        }
    }

    /// <summary>
    /// Shared widgets retain independent colors across content changes and theme-only dirty paints that bypass cache checks.
    /// </summary>
    [TestMethod]
    public async Task SharedWidget_TracksEachNodesInheritedColors()
    {
        var phases = new ConcurrentQueue<int>();
        var widget = new TranscriptLineWidget(TranscriptLine.Of(LineKind.Output, "shared"), 40).CacheRendering();
        var first = Hex1bColor.FromRgb(170, 180, 190);
        var second = Hex1bColor.FromRgb(190, 180, 170);
        var theme = new Hex1bTheme("initial");
        var changedBackground = Hex1bColor.FromRgb(20, 30, 40);
        var phase = 0;
        Hex1bApp app = null!;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithHeadless().WithDimensions(40, 10)
            .WithHex1bApp(options =>
            {
                options.EnableRenderCaching = true;
                options.ThemeProvider = () => Volatile.Read(ref theme);
            }, instance =>
            {
                app = instance;
                return ctx =>
                {
                    while (phases.TryDequeue(out var requested))
                    {
                        phase = requested;
                        if (phase == 3) widget = widget with { Line = TranscriptLine.Of(LineKind.Output, "changed") };
                    }
                    var originalColors = phase is 0 or 1 or 3 or 5;
                    return ctx.VStack(v =>
                    [
                        v.ThemePanel(theme => theme.Set(GlobalTheme.ForegroundColor, originalColors ? first : second),
                            v.VStack(row => [widget, row.Text("top " + phase)])),
                        v.ThemePanel(theme => theme.Set(GlobalTheme.ForegroundColor, originalColors ? second : first),
                            v.VStack(row => [widget, row.Text("bottom " + phase)])),
                    ]);
                };
            }).Build();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var run = terminal.RunAsync(cancellation.Token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));
        try
        {
            await auto.WaitUntilTextAsync("bottom 0");
            for (var requested = 1; requested <= 6; requested++)
            {
                if (requested == 5)
                {
                    Volatile.Write(ref theme, new Hex1bTheme("replacement").Set(GlobalTheme.BackgroundColor, changedBackground));
                }
                phases.Enqueue(requested);
                app.Invalidate();
                await auto.WaitUntilTextAsync("bottom " + requested);
                using var snapshot = auto.CreateSnapshot();
                Assert.AreEqual(requested < 3 ? "shared" : "changed", AppTest.Row(snapshot, 0));
                Assert.AreEqual(requested < 3 ? "shared" : "changed", AppTest.Row(snapshot, 2));
                var originalColors = requested is 1 or 3 or 5;
                Assert.AreEqual(originalColors ? first : second, snapshot.GetCell(0, 0).Foreground, "top phase " + requested);
                Assert.AreEqual(originalColors ? second : first, snapshot.GetCell(0, 2).Foreground, "bottom phase " + requested);
                if (requested >= 5)
                {
                    Assert.AreEqual(changedBackground, snapshot.GetCell(0, 0).Background);
                    Assert.AreEqual(changedBackground, snapshot.GetCell(0, 2).Background);
                }
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private static void AssertCell(Hex1bTerminalSnapshot snapshot, string text, Hex1bColor foreground, Hex1bColor background)
    {
        var hit = Assert.ContainsSingle(snapshot.FindText(text));
        var cell = snapshot.GetCell(hit.Column, hit.Line);
        Assert.AreEqual(foreground, cell.Foreground, text);
        Assert.AreEqual(background, cell.Background, text);
    }
}
