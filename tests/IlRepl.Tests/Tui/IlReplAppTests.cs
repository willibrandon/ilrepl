using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Drives the real terminal UI on the Hex1b headless emulator with a real host process behind it.
/// </summary>
[TestClass]
public sealed class IlReplAppTests
{
    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Typing instructions echoes the stack, and ret prints the value.
    /// </summary>
    /// <param name="width">The terminal width.</param>
    /// <param name="height">The terminal height.</param>
    [TestMethod]
    [DataRow(80, 24)]
    [DataRow(120, 40)]
    public async Task TypeInstructions_ShowsStackAndResult(int width, int height)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(width, height)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4 6", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");
        await auto.TypeAsync("ldc.i4 7", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32, int32]");
        await auto.TypeAsync("mul", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("stack [int32]"), description: "status bar shows one value");
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 42 : int32");
        await auto.WaitUntilTextAsync("il[2]>");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// The completion palette opens on a prefix, Escape dismisses it, typing reopens it, the arrows
    /// move the highlight, and Tab accepts the highlighted opcode.
    /// </summary>
    [TestMethod]
    public async Task Palette_DismissReopenSelectAndAccept()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("ldc.i4.", ct: ct);
        await auto.WaitUntilTextAsync("opcodes 1/11");
        await auto.WaitUntilTextAsync("❯ ldc.i4.0");
        await auto.EscapeAsync(ct: ct);
        await auto.WaitUntilNoTextAsync("opcodes 1/11");

        // An exact opcode has nothing to complete, so the palette stays closed.
        await auto.TypeAsync("s", ct: ct);
        await auto.WaitUntilTextAsync("il[1]> ldc.i4.s");
        await auto.WaitUntilNoTextAsync("opcodes");

        // Editing the word reopens it; the arrows move the highlight; Tab accepts.
        await auto.BackspaceAsync(ct: ct);
        await auto.WaitUntilTextAsync("opcodes 1/11");
        await auto.DownAsync(ct: ct);
        await auto.DownAsync(ct: ct);
        await auto.WaitUntilTextAsync("opcodes 3/11");
        await auto.WaitUntilTextAsync("❯ ldc.i4.2");
        await auto.TabAsync(ct: ct);
        await auto.WaitUntilNoTextAsync("opcodes 3/11");
        await auto.WaitUntilTextAsync("il[1]> ldc.i4.2");
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 2 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Up recalls the previous line and errors show in red.
    /// </summary>
    [TestMethod]
    public async Task History_And_ErrorColor()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("lcd.i4 1", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("did you mean 'ldc.i4'");

        using (var snapshot = auto.CreateSnapshot())
        {
            Assert.IsTrue(snapshot.HasForegroundColor(SpanPalette.Color(SpanStyle.Error)), "the error label should use the error color");
        }

        await auto.UpAsync(ct: ct);
        await auto.WaitUntilAsync(s => s.ContainsText("il[1]> lcd.i4 1"), description: "history recalled into the prompt");
        await auto.HomeAsync(ct: ct);
        await auto.DeleteAsync(ct: ct);
        await auto.DeleteAsync(ct: ct);
        await auto.DeleteAsync(ct: ct);
        await auto.TypeAsync("ldc", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// A mouse click on the transcript does not take keyboard focus away from the prompt.
    /// </summary>
    [TestMethod]
    public async Task ClickOnTranscript_KeepsTypingAtThePrompt()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .WithMouse()
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.ClickAtAsync(40, 10, ct: ct);
        await auto.TypeAsync("ldc.i4 5", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("[int32]");
        await auto.ClickAtAsync(20, 5, ct: ct);
        await auto.TypeAsync("ret", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("= 5 : int32");

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }

    /// <summary>
    /// Ctrl+L clears the transcript back to the banner.
    /// </summary>
    [TestMethod]
    public async Task CtrlL_ClearsTranscript()
    {
        var ct = TestContext.CancellationToken;
        await using var engine = await HostPaths.StartEngineAsync(ct);
        var transcript = new Transcript();
        await using var terminal = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript)
            .WithHeadless()
            .WithDimensions(100, 30)
            .Build();

        var run = terminal.RunAsync(ct);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(15));

        await auto.WaitUntilTextAsync("il[1]>");
        await auto.TypeAsync("nop", ct: ct);
        await auto.EnterAsync(ct: ct);
        await auto.WaitUntilTextAsync("il[1]> nop");
        await auto.Ctrl().KeyAsync(Hex1bKey.L, ct: ct);
        await auto.WaitUntilNoTextAsync("il[1]> nop");
        await auto.WaitUntilTextAsync(IlReplApp.Banner[..20]);

        await auto.Ctrl().KeyAsync(Hex1bKey.Q, ct: ct);
        await run;
    }
}
