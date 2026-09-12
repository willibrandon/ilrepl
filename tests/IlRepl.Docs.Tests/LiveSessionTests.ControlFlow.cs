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
        const int Partitions = 4;
        var pages = await Task.WhenAll(Enumerable.Range(0, Partitions)
            .Select(_ => OpenSessionAsync(context)));
        var indexed = ControlFlowExamples.All.Select((example, index) => (Example: example, Index: index));
        await Task.WhenAll(pages.Select((page, partition) => RunCorpusPartitionAsync(
            page, indexed.Where(item => item.Index % Partitions == partition))));
    }

    private async Task RunCorpusPartitionAsync(IPage page,
        IEnumerable<(ControlFlowExample Example, int Index)> examples)
    {
        page.Console += (_, message) => TestContext.WriteLine(message.Text);
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        foreach (var (example, index) in examples)
        {
            TestContext.CancellationToken.ThrowIfCancellationRequested();
            TestContext.WriteLine(example.Name);
            var genericType = "FlowGeneric" + index;
            var source = example.GenericParameters.Length == 0
                ? example.Source : example.Source.Replace("FlowGeneric", genericType, StringComparison.Ordinal);
            var call = example.GenericParameters.Length == 0
                ? example.Call : example.Call.Replace("FlowGeneric", genericType, StringComparison.Ordinal);
            await PasteAsync(page, source);
            await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("Enter sends", options);
            await ArmSubmissionOutputAsync(page);
            await page.Keyboard.PressAsync("Enter");
            if (example.Accepted)
            {
                await SubmissionSettledAsync(page);
                var committed = example.GenericParameters.Length == 0
                    ? "end of method " + example.Name : "end of class " + genericType;
                await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync(committed, options);
                var expected = 1_000 + index;
                await RunCorpusCellAsync(page,
                    "ldc.i4.1\n" + call + $"\nldc.i4 {expected - 42}\nadd\nret", expected);
            }
            else
            {
                await ReturnedBodyAsync(page);
                await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync(example.Finding, options);
                await ClearPromptAsync(page);
            }

            await ResetSessionAsync(page);
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
    /// Top-level custom modifiers on instruction type operands survive browser emission.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_ModifiedTypeOperandsMatchDesktop(string browser)
    {
        const string Modifier = "[System.Runtime]System.Runtime.CompilerServices.IsVolatile";
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await PasteAsync(page, $"ldc.i4.1\nnewarr int32 modreq({Modifier}) modopt({Modifier})\npop\n"
            + $"ldtoken int32 modopt({Modifier})\ncall Type::GetTypeFromHandle(RuntimeTypeHandle)\nret");
        await Assertions.Expect(terminal).ToContainTextAsync("Enter sends 6 lines", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("typeof(int32)", options);
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
    /// Argument writes and filter decisions carry receiver provenance in the browser runtime.
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
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.AddressSource("stind.ref")));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ArgumentSource(true, true)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class FlowArgument", options);
        foreach (var instruction in new[] { "initobj", "ldftn", "ldstr", "ldtoken", "refanytype", "sizeof" })
        {
            await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.NonThrowingInstructionSource(instruction)));
            await page.Keyboard.PressAsync("Enter");
            var type = instruction[0].ToString().ToUpperInvariant() + instruction[1..];
            await Assertions.Expect(page.Locator("#terminal"))
                .ToContainTextAsync($"end of class NonThrowing{type}Receiver", options);
        }
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ThrowingLdvirtftnSource()));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.FilterSource(false)));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.FilterSource(true)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class FlowFilterArgument", options);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.SelectiveFilterSource()));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class SelectiveFilterArgument", options);
        await TypeLineAsync(page, "ldnull");
        await TypeLineAsync(page, "ldc.i4.1");
        await TypeLineAsync(page,
            "newobj instance void SelectiveFilterArgument::.ctor(class SelectiveFilterArgument, bool)");
        await TypeLineAsync(page, "ldfld int32 SelectiveFilterArgument::Value");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 42 : int32", options);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.CorrelatedReceiverFilterSource()));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class CorrelatedFilterArgument", options);
        await TypeLineAsync(page, "ldnull");
        await TypeLineAsync(page, "ldc.i4.1");
        await TypeLineAsync(page,
            "newobj instance void CorrelatedFilterArgument::.ctor(class CorrelatedFilterArgument, bool)");
        await TypeLineAsync(page, "ldfld int32 CorrelatedFilterArgument::Value");
        await TypeLineAsync(page, "ret");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("= 42 : int32", options);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.SiblingFilterSource()));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.RestoringFilterSource(true)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class RestoringFilterArgument", options);
    }

    /// <summary>
    /// Receiver changes made by a finally handler reach the leave target in browser analysis.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_FinallyWriteMatchesDesktop(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.FinallySource(false)));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("error:", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.FinallySource(true)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class FlowFinallyArgument", options);
        foreach (var source in new[]
        {
            ControlFlowReceiverExamples.ConstantBranchStackSource(),
            ControlFlowReceiverExamples.ConstantBranchAfterUnwindSource(),
            ControlFlowReceiverExamples.ConstantBranchAtEndfilterSource(),
            ControlFlowReceiverExamples.ConstantBranchAtEndfinallySource(),
        })
        {
            await PasteAsync(page, string.Join('\n', source));
            await ArmSubmissionOutputAsync(page, "stack underflow");
            await page.Keyboard.PressAsync("Enter");
            await ReturnedBodyAfterOutputAsync(page, "stack underflow");
            await ClearPromptAsync(page);
        }
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.NestedNonCompletingFinallySource()));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class NestedFinallyArgument", options);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ExceptionUnwindSource(false, true, true)));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ExceptionUnwindSource(true, false, false)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class FlowExceptionUnwindArgument", options);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.UnwindHandlerStoreSource(true, true)));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.FilterPathUnwindHandlerSource()));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ConditionalFinalizerSource(false)));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ConditionalFinalizerSource(true)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class ConditionalFinalizer", options);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.CorrelatedSwitchFinalizerSource(true)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal"))
            .ToContainTextAsync("end of class CorrelatedSwitchFinallyArgument", options);
    }

    private static async Task ArmSubmissionOutputAsync(IPage page, string? expected = null)
    {
        await page.EvaluateAsync("""
            expected => {
              const terminal = window.ilreplTerminal;
              if (!window.ilreplControlFlowWriteWrapped) {
                const write = terminal.write.bind(terminal);
                terminal.write = (data, callback) => {
                  return write(data, () => {
                    window.ilreplControlFlowWriteCount++;
                    window.ilreplControlFlowLastWrite = performance.now();
                    const output = typeof data === 'string' ? data : new TextDecoder().decode(data);
                    window.ilreplControlFlowOutput = (window.ilreplControlFlowOutput ?? '') + output;
                    const buffer = terminal.buffer.active;
                    const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
                    window.ilreplControlFlowSawBusy ||= status.includes('updating') || status.includes('sending');
                    if (callback) callback();
                  });
                };
                window.ilreplControlFlowWriteWrapped = true;
              }

              return new Promise(resolve => terminal.write('', () => {
                window.ilreplControlFlowWriteCount = 0;
                window.ilreplControlFlowLastWrite = 0;
                window.ilreplControlFlowSawBusy = false;
                window.ilreplControlFlowOutput = '';
                const buffer = terminal.buffer.active;
                const text = Array.from({ length: buffer.length }, (_, row) =>
                  buffer.getLine(row)?.translateToString(true) ?? '').join('\n');
                window.ilreplControlFlowExpectedCount = expected === null ? 0 : text.split(expected).length - 1;
                resolve();
              }));
            }
            """, expected);
    }

    private static Task<IJSHandle> ReturnedBodyAsync(IPage page) => page.WaitForFunctionAsync("""
        () => {
          const terminal = window.ilreplTerminal;
          const buffer = terminal.buffer.active;
          const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
          return window.ilreplControlFlowSawBusy && window.ilreplControlFlowWriteCount > 0
            && performance.now() - window.ilreplControlFlowLastWrite >= 100 && status.includes('editing ')
            && !status.includes('updating') && !status.includes('sending');
        }
        """, null, new() { PollingInterval = 16, Timeout = 30_000 });

    private static Task<IJSHandle> ReturnedBodyAfterOutputAsync(IPage page, string expected) => page.WaitForFunctionAsync("""
        expected => {
          const terminal = window.ilreplTerminal;
          const buffer = terminal.buffer.active;
          const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
          const text = Array.from({ length: buffer.length }, (_, row) =>
            buffer.getLine(row)?.translateToString(true) ?? '').join('\n');
          const count = text.split(expected).length - 1;
          return window.ilreplControlFlowWriteCount > 0 && window.ilreplControlFlowOutput.includes('error:')
            && count > window.ilreplControlFlowExpectedCount
            && performance.now() - window.ilreplControlFlowLastWrite >= 100 && status.includes('editing ')
            && !status.includes('updating') && !status.includes('sending');
        }
        """, expected, new() { PollingInterval = 16, Timeout = 30_000 });

    private static async Task ResetSessionAsync(IPage page)
    {
        await PasteAsync(page, ".reset");
        await ReadyToSubmitResetAsync(page);
        await ArmSubmissionOutputAsync(page);
        await page.Keyboard.PressAsync("Enter");
        await ResetCompletedAsync(page);
    }

    private static Task<IJSHandle> ReadyToSubmitResetAsync(IPage page) => page.WaitForFunctionAsync("""
        () => {
          const terminal = window.ilreplTerminal;
          const buffer = terminal.buffer.active;
          const first = buffer.baseY;
          const lines = Array.from({ length: terminal.rows }, (_, row) =>
            buffer.getLine(first + row)?.translateToString(true) ?? '');
          const prompt = lines.findLast(line => /il\[\d+\]>/.test(line));
          const status = lines.at(-1) ?? '';
          return /^il\[\d+\]> \.reset\s*$/.test(prompt ?? '')
            && !status.includes('updating') && !status.includes('sending');
        }
        """, null, new() { PollingInterval = 16, Timeout = 30_000 });

    private static async Task RunCorpusCellAsync(IPage page, string source, int expected)
    {
        await PasteAsync(page, source);
        await ArmSubmissionOutputAsync(page);
        await page.Keyboard.PressAsync("Enter");
        await SubmissionSettledAsync(page);
        await page.WaitForFunctionAsync("""
            expected => {
              const terminal = window.ilreplTerminal;
              return Array.from({ length: terminal.buffer.active.length }, (_, row) =>
                terminal.buffer.active.getLine(row)?.translateToString(true) ?? '')
                .some(line => line.includes(expected));
            }
            """, $"= {expected} : int32", new() { PollingInterval = 16, Timeout = 30_000 });
    }

    private static Task<IJSHandle> SubmissionSettledAsync(IPage page) => page.WaitForFunctionAsync("""
        () => {
          const terminal = window.ilreplTerminal;
          const buffer = terminal.buffer.active;
          const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
          return window.ilreplControlFlowSawBusy && window.ilreplControlFlowWriteCount > 0
            && performance.now() - window.ilreplControlFlowLastWrite >= 100 && !status.includes('editing ')
            && !status.includes('updating') && !status.includes('sending');
        }
        """, null, new() { PollingInterval = 16, Timeout = 30_000 });

    private static async Task ResetCompletedAsync(IPage page)
    {
        try
        {
            await page.WaitForFunctionAsync("""
                () => {
                  const terminal = window.ilreplTerminal;
                  const buffer = terminal.buffer.active;
                  const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
                  const lines = Array.from({ length: buffer.length }, (_, row) =>
                    buffer.getLine(row)?.translateToString(true) ?? '');
                  const reset = lines.findLastIndex(line => line.includes('.reset'));
                  const cleared = lines.slice(reset + 1)
                    .some(line => line.includes('cell, declarations, methods, and types cleared'));
                  return reset >= 0 && cleared && window.ilreplControlFlowWriteCount > 0
                    && performance.now() - window.ilreplControlFlowLastWrite >= 100 && !status.includes('editing ')
                    && !status.includes('updating') && !status.includes('sending');
                }
                """, null, new() { PollingInterval = 16, Timeout = 30_000 });
        }
        catch (TimeoutException exception)
        {
            var state = await page.EvaluateAsync<string>("""
                () => {
                  const terminal = window.ilreplTerminal;
                  const buffer = terminal.buffer.active;
                  const count = Math.min(buffer.length, 20);
                  const rows = Array.from({ length: count }, (_, index) =>
                    buffer.getLine(buffer.length - count + index)?.translateToString(true) ?? '');
                  return JSON.stringify({
                    output: window.ilreplControlFlowOutput,
                    writes: window.ilreplControlFlowWriteCount,
                    age: performance.now() - window.ilreplControlFlowLastWrite,
                    rows
                  });
                }
                """);
            throw new InvalidOperationException("reset did not settle: " + state, exception);
        }
    }
}
