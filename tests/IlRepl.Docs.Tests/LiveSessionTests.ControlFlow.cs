using IlRepl.Tests.Shared;
using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    /// <summary>
    /// An opcode prefix remains unfinished until completed, without suggesting an unrelated instruction.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_OpcodePrefixCompletesWithoutATypoSuggestion(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await page.Keyboard.TypeAsync("ldc");
        await Assertions.Expect(terminal).ToContainTextAsync("finish opcode 'ldc'", options);
        await Assertions.Expect(terminal).ToContainTextAsync("incomplete on line 1", options);
        Assert.DoesNotContain("did you mean", await BufferTextAsync(page));
        await page.Keyboard.TypeAsync(".i4 42");
        await Assertions.Expect(terminal).Not.ToContainTextAsync("incomplete on line", options);
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 42 : int32", options);
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Browser Mono accepts and refuses the same source corpus checked against the desktop verifier.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(300_000, CooperativeCancellation = true)]
    public async Task ControlFlow_CorpusMatchesDesktop(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        page.Console += (_, message) => TestContext.WriteLine(message.Text);
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        var session = 1;
        foreach (var example in ControlFlowExamples.All)
        {
            TestContext.CancellationToken.ThrowIfCancellationRequested();
            TestContext.WriteLine(example.Name);
            await PasteAsync(page, example.Source);
            await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("Enter sends", options);
            await ArmSubmissionOutputAsync(page);
            await page.Keyboard.PressAsync("Enter");
            if (example.Accepted)
            {
                var committed = example.GenericParameters.Length == 0 ? "end of method " + example.Name : "end of class FlowGeneric";
                await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync(committed, options);
                await TypeLineAsync(page, "ldc.i4.1");
                await TypeLineAsync(page, example.Call);
                await TypeLineAsync(page, "ret");
                await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 42 : int32", options);
            }
            else
            {
                await ReturnedBodyAsync(page);
                await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync(example.Finding, options);
                await ClearPromptAsync(page);
                await TypeLineAsync(page, "ldc.i4.s 42");
                await TypeLineAsync(page, "ret");
                await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 42 : int32", options);
            }

            await page.Keyboard.PressAsync("Control+q");
            await WaitForSessionAsync(page, ++session, 30_000);
            await ClickIntoTerminalAsync(page);
        }
    }

    /// <summary>
    /// Browser Mono refuses transfers and prefixes that cross protected-region boundaries.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_ProtectedRegionBoundariesMatchDesktop(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await PasteAsync(page, ".method int32 LeaveIntoTry() {\nleave INSIDE\n.try {\nINSIDE: leave DONE\n"
            + "} finally {\nendfinally\n}\nDONE: ldc.i4.s 42\nret\n}");
        await Assertions.Expect(terminal).ToContainTextAsync("leave cannot transfer control", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("error: leave cannot transfer control", options);
        await page.Keyboard.PressAsync("Control+q");
        await WaitForSessionAsync(page, 2, 30_000);
        await ClickIntoTerminalAsync(page);
        await PasteAsync(page, ".method int32 PrefixAcrossTry() {\nvolatile.\n.try {\nleave DONE\n"
            + "} finally {\nendfinally\n}\nDONE: ldc.i4.s 42\nret\n}");
        await Assertions.Expect(terminal).ToContainTextAsync("protected-region boundary", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("error: a protected-region boundary", options);
    }

    /// <summary>
    /// Closing a structured finally clears its remaining stack in browser Mono.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_ImplicitEndfinallyClearsStack(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await PasteAsync(page, ".method int32 ImplicitEndfinally(int32 n) {\n.try {\nleave DONE\n"
            + "} finally {\nldc.i4.1\n}\nDONE: ldc.i4.s 42\nret\n}");
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 9 lines", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of method ImplicitEndfinally", options);
        await TypeLineAsync(page, "ldc.i4.1");
        await TypeLineAsync(page, "call int32 ImplicitEndfinally(int32)");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 42 : int32", options);
    }

    /// <summary>
    /// The return synthesized when a method closes completes its trailing tail call in browser Mono.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_ImplicitReturnCompletesTailCall(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await PasteAsync(page, ".method int32 Tail(int32 value) {\nldarg value\ntail.\n"
            + "call int32 [System.Runtime]System.Math::Abs(int32)\n}");
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 5 lines", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("end of method Tail", options);
        await TypeLineAsync(page, "ldc.i4.s -42");
        await TypeLineAsync(page, "call int32 Tail(int32)");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync("= 42 : int32", options);
    }

    /// <summary>
    /// A browser cell returns a reference tail call directly and refuses implicit or inline boxing.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_CellTailReturnMatchesEmission(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await PasteAsync(page, "ldstr \"forty\"\nldstr \"two\"\ntail.\ncall string string::Concat(string, string)");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("4 instructions", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("= \"fortytwo\" : string", options);
        await TypeLineAsync(page, "ldc.i4.0");
        await TypeLineAsync(page, "brtrue PENDING");
        await TypeLineAsync(page, "ldc.i4.s -42");
        await TypeLineAsync(page, "tail.");
        await TypeLineAsync(page, "call int32 [System.Runtime]System.Math::Abs(int32)");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(terminal).ToContainTextAsync(
            "error: a tail call in the cell must return object directly", options);
        await TypeLineAsync(page, ".clear");
        await PasteAsync(page, "ldc.i4.s -42\ntail.\ncall int32 [System.Runtime]System.Math::Abs(int32)");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("3 instructions", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("error: a tail call must be followed by ret", options);
    }

    /// <summary>
    /// A later branch updates an earlier caret stack, and a correction removes the diagnostic before submission.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task ControlFlow_CaretAndCorrectionUseWholeDocument(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        page.Console += (_, message) => TestContext.WriteLine(message.Text);
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        var wrong = ControlFlowExamples.All.Single(example => example.Name == "WrongDepth");
        await PasteAsync(page, wrong.Source);
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("incompatible stacks", options);
        await page.Keyboard.PressAsync("F8");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("stack before invalid", options);
        await ClearPromptAsync(page);
        var good = ControlFlowExamples.All.Single(example => example.Name == "Diamond");
        await PasteAsync(page, good.Source);
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("incompatible stacks", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of method Diamond", options);
        await TypeLineAsync(page, "ldc.i4.1");
        await TypeLineAsync(page, "call int32 Diamond(int32)");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 42 : int32", options);
    }

    /// <summary>
    /// A later constructor branch rechecks an earlier readonly store before Mono creates the class.
    /// </summary>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_ReadonlyReceiverMatchesDesktop(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.Source(false)));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await page.Keyboard.PressAsync("F8");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("stack before", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("error:", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.Source(true)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class FlowReceiver", options);
        await TypeLineAsync(page, "ldnull");
        await TypeLineAsync(page, "newobj instance void FlowReceiver::.ctor(class FlowReceiver)");
        await TypeLineAsync(page, "ldfld int32 FlowReceiver::Value");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 42 : int32", options);
    }

    /// <summary>
    /// Replacing argument zero removes its original-receiver provenance in the browser runtime.
    /// </summary>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_ArgumentWriteMatchesDesktop(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ArgumentSource(false, true)));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ArgumentSource(true, true)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class FlowArgument", options);
    }

    private static async Task ArmSubmissionOutputAsync(IPage page)
    {
        await page.EvaluateAsync("""
            () => {
              const terminal = window.ilreplTerminal;
              window.ilreplControlFlowWriteCount = 0;
              window.ilreplControlFlowLastWrite = 0;
              if (!window.ilreplControlFlowWriteWrapped) {
                const write = terminal.write.bind(terminal);
                terminal.write = (data, callback) => write(data, () => {
                  window.ilreplControlFlowWriteCount++;
                  window.ilreplControlFlowLastWrite = performance.now();
                  if (callback) callback();
                });
                window.ilreplControlFlowWriteWrapped = true;
              }
            }
            """);
    }

    private static Task<IJSHandle> ReturnedBodyAsync(IPage page) => page.WaitForFunctionAsync("""
        () => {
          const terminal = window.ilreplTerminal;
          const status = terminal.buffer.active.getLine(terminal.rows - 1)?.translateToString(true) ?? '';
          return window.ilreplControlFlowWriteCount > 0 && performance.now() - window.ilreplControlFlowLastWrite >= 100
            && status.includes('editing ') && !status.includes('updating') && !status.includes('sending');
        }
        """, null, new() { PollingInterval = 16, Timeout = 30_000 });
}
