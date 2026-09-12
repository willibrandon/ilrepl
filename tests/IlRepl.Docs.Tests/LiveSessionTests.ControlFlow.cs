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
        var launched = GetBrowser(browser);
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
    /// Browser preview and submission reject an inaccessible type retained in an indirect call signature.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_CalliSignatureChecksAccessibilityWhileEditing(string browser)
    {
        var launched = GetBrowser(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await TypeLineAsync(page, ".class public Outer {");
        await TypeLineAsync(page, ".class nested private Inner { }");
        await TypeLineAsync(page, "}");
        await Assertions.Expect(terminal).ToContainTextAsync("end of class Outer", options);
        await TypeLineAsync(page, "ldc.i4.0");
        await TypeLineAsync(page, "conv.u");
        await page.Keyboard.TypeAsync("calli void modopt(Outer/Inner)()");
        await Assertions.Expect(terminal).ToContainTextAsync("Outer/Inner is nested private", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("error: Outer/Inner is nested private", options);
    }

    private async Task RunControlFlowCorpusAsync(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        const int Partitions = 2;
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
            await ReadyToSubmitCorpusAsync(page);
            if (example.Accepted)
            {
                var committed = example.GenericParameters.Length == 0
                    ? "end of method " + example.Name : "end of class " + genericType;
                await SubmitAcceptedCorpusAsync(page, committed);
                await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync(committed, options);
                var expected = 1_000 + index;
                await RunCorpusCellAsync(page,
                    "ldc.i4.1\n" + call + $"\nldc.i4 {expected - 42}\nadd\nret", expected);
            }
            else
            {
                var abandoned = example.GenericParameters.Length == 0
                    ? $"method {example.Name} abandoned" : $"class {genericType} abandoned";
                await SubmitRejectedCorpusAsync(page, example.Finding, abandoned,
                    source.TrimEnd('\r', '\n').Count(character => character == '\n') + 1);
                await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync(example.Finding, options);
                await ResetReturnedCorpusAsync(page);
                continue;
            }

            await ResetSessionAsync(page);
        }
    }

    private static async Task SubmitAcceptedCorpusAsync(IPage page, string committed)
    {
        await ArmSubmissionOutputAsync(page);
        await SendTerminalInputAsync(page, "\r");
        try
        {
            await page.WaitForFunctionAsync("""
                committed => {
                  const terminal = window.ilreplTerminal;
                  const buffer = terminal.buffer.active;
                  const prompt = buffer.getLine(buffer.baseY + terminal.rows - 2)?.translateToString(true).trim() ?? '';
                  const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
                  const found = Array.from({ length: buffer.length }, (_, row) =>
                    buffer.getLine(row)?.translateToString(true) ?? '').some(line => line.includes(committed));
                  return found && /^il\[\d+\]>$/.test(prompt)
                    && !status.includes('editing ') && !status.includes('updating') && !status.includes('sending')
                    && !status.includes('cancelling') && !status.includes('Ctrl+C cancels');
                }
                """, committed, new() { PollingInterval = 16, Timeout = 30_000 });
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException($"{committed} was not observed: {await BrowserWaitStateAsync(page)}", exception);
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
        var launched = GetBrowser(browser);
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
    /// Filters reject nested protected regions before Mono compiles the submitted constructor.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ControlFlow_FilterCannotContainTry(string browser)
    {
        var launched = GetBrowser(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.NestedFilterCatchSource(true)));
        await Assertions.Expect(terminal).ToContainTextAsync("a try region is not allowed inside a filter", options);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(terminal).ToContainTextAsync("error: a try region is not allowed inside a filter", options);
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
        var launched = GetBrowser(browser);
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
        var launched = GetBrowser(browser);
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
        var launched = GetBrowser(browser);
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
        var launched = GetBrowser(browser);
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
        var launched = GetBrowser(browser);
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
        var launched = GetBrowser(browser);
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
        var launched = GetBrowser(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var options = new LocatorAssertionsToContainTextOptions { Timeout = 30_000 };
        foreach (var use in new[] { "pop", "load" })
        {
            await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.NonMutatingAddressSource(use)));
            await page.Keyboard.PressAsync("Enter");
            var suffix = char.ToUpperInvariant(use[0]) + use[1..];
            await Assertions.Expect(page.Locator("#terminal"))
                .ToContainTextAsync($"end of class FlowAddress{suffix}Argument", options);
        }
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.AddressSource("stind.ref")));
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
        await ClearPromptAsync(page);
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ArgumentSource(true, true)));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("end of class FlowArgument", options);
        foreach (var instruction in new[] { "constrained.", "initobj", "isinst", "ldftn", "ldstr", "ldtoken", "refanytype", "sizeof" })
        {
            await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.NonThrowingInstructionSource(instruction)));
            await page.Keyboard.PressAsync("Enter");
            var type = instruction == "constrained."
                ? "Constrained" : instruction[0].ToString().ToUpperInvariant() + instruction[1..];
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
        var launched = GetBrowser(browser);
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
        foreach (var (source, abandoned) in new[]
        {
            (ControlFlowReceiverExamples.ConstantBranchStackSource(), "class ConstantBranchStack abandoned"),
            (ControlFlowReceiverExamples.ConstantBranchAfterUnwindSource(), "class ConstantBranchAfterUnwind abandoned"),
            (ControlFlowReceiverExamples.ConstantBranchAtEndfilterSource(), "class ConstantBranchAtEndfilter abandoned"),
            (ControlFlowReceiverExamples.ConstantBranchAtEndfinallySource(), "class ConstantBranchAtEndfinally abandoned"),
        })
        {
            await PasteAsync(page, string.Join('\n', source));
            await ArmSubmissionOutputAsync(page);
            await page.Keyboard.PressAsync("Enter");
            await ReturnedBodyAfterOutputAsync(page, "stack underflow", abandoned, source.Length);
            await ClearPromptAsync(page);
        }
        foreach (var source in new[]
        {
            ControlFlowReceiverExamples.ConstantBranchReceiverSource(),
            ControlFlowReceiverExamples.ConstantBranchReceiverMergeSource(),
        })
        {
            await PasteAsync(page, string.Join('\n', source));
            await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync("through this", options);
            await ClearPromptAsync(page);
        }
        await PasteAsync(page, string.Join('\n', ControlFlowReceiverExamples.ConstantBranchThisReceiverSource()));
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync(
            "end of class ConstantBranchThisReceiver", options);
        await TypeLineAsync(page, ".reset");
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

    private static async Task ArmSubmissionOutputAsync(IPage page)
    {
        await page.EvaluateAsync("""
            () => {
              const terminal = window.ilreplTerminal;
              if (!window.ilreplControlFlowWriteWrapped) {
                const write = terminal.write.bind(terminal);
                terminal.write = (data, callback) => {
                  return write(data, () => {
                    window.ilreplControlFlowWriteCount++;
                    window.ilreplControlFlowLastWrite = performance.now();
                    const output = typeof data === 'string' ? data : new TextDecoder().decode(data);
                    window.ilreplControlFlowOutput = (window.ilreplControlFlowOutput ?? '') + output;
                    if (callback) callback();
                  });
                };
                window.ilreplControlFlowWriteWrapped = true;
              }

              return new Promise(resolve => terminal.write('', () => {
                window.ilreplControlFlowWriteCount = 0;
                window.ilreplControlFlowLastWrite = 0;
                window.ilreplControlFlowArmed = performance.now();
                window.ilreplControlFlowOutput = '';
                resolve();
              }));
            }
            """);
    }

    private static async Task ReadyToSubmitCorpusAsync(IPage page)
    {
        await ArmSubmissionOutputAsync(page);
        await page.WaitForFunctionAsync("""
            () => {
              const terminal = window.ilreplTerminal;
              const buffer = terminal.buffer.active;
              const prompt = buffer.getLine(buffer.baseY + terminal.rows - 2)?.translateToString(true).trimEnd() ?? '';
              const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
              return prompt.endsWith('> }') && status.includes('Enter sends')
                && !status.includes('updating') && !status.includes('sending') && !status.includes('cancelling')
                && !status.includes('Ctrl+C cancels');
            }
            """, null, new() { PollingInterval = 16, Timeout = 30_000 });
    }

    private static async Task SubmitRejectedCorpusAsync(IPage page, string finding, string abandoned, int lineCount)
    {
        await ArmSubmissionOutputAsync(page);
        await SendTerminalInputAsync(page, "\r");
        try
        {
            await page.WaitForFunctionAsync("""
                value => {
                  const args = value.split('\n');
                  const terminal = window.ilreplTerminal;
                  const buffer = terminal.buffer.active;
                  const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
                  const output = window.ilreplControlFlowOutput;
                  const plainOutput = output
                    .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '').replace(/\s+/g, ' ');
                  const completed = output.includes('0/' + args[0]) || plainOutput.includes(args[2]);
                  return completed && plainOutput.includes(args[1])
                    && window.ilreplControlFlowWriteCount > 0
                    && status.includes('editing ')
                    && !status.includes('updating') && !status.includes('sending') && !status.includes('cancelling')
                    && !status.includes('Ctrl+C cancels');
                }
                """, $"{lineCount}\n{finding}\n{abandoned}", new() { PollingInterval = 16, Timeout = 30_000 });
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"the rejection containing {finding} did not settle: {await BrowserWaitStateAsync(page)}", exception);
        }
    }

    private static Task<string> BrowserWaitStateAsync(IPage page) => page.EvaluateAsync<string>("""
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

    private static async Task ReturnedBodyAfterOutputAsync(IPage page, string expected, string abandoned, int lineCount)
    {
        try
        {
            await page.WaitForFunctionAsync("""
                value => {
                  const args = value.split('\n');
                  const terminal = window.ilreplTerminal;
                  const buffer = terminal.buffer.active;
                  const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
                  const output = window.ilreplControlFlowOutput;
                  const plainOutput = output
                    .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '').replace(/\s+/g, ' ');
                  const completed = output.includes('0/' + args[0]) || plainOutput.includes(args[2]);
                  return completed && window.ilreplControlFlowWriteCount > 0 && plainOutput.includes(args[1])
                    && status.includes('editing ')
                    && !status.includes('updating') && !status.includes('sending') && !status.includes('cancelling')
                    && !status.includes('Ctrl+C cancels');
                }
                """, $"{lineCount}\n{expected}\n{abandoned}", new() { PollingInterval = 16, Timeout = 30_000 });
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"the returned body containing {expected} did not settle: {await BrowserWaitStateAsync(page)}", exception);
        }
    }

    private static async Task ResetSessionAsync(IPage page)
    {
        await EmptyPromptAsync(page);
        await SubmitResetAsync(page);
    }

    private static async Task ResetReturnedCorpusAsync(IPage page)
    {
        await SubmitResetAsync(page, clearReturned: true);
    }

    private static async Task SubmitResetAsync(IPage page, bool clearReturned = false)
    {
        await InputIdleAsync(page);
        var marker = "reset-" + Guid.NewGuid().ToString("N");
        await ArmSubmissionOutputAsync(page);
        await SendTerminalInputAsync(page, (clearReturned ? "\x03" : string.Empty) + $".reset // {marker}");
        await ReadyToSubmitResetAsync(page, marker);
        await ArmSubmissionOutputAsync(page);
        await SendTerminalInputAsync(page, "\r");
        await ResetCompletedAsync(page, marker);
    }

    private static async Task ReadyToSubmitResetAsync(IPage page, string marker)
    {
        try
        {
            await page.WaitForFunctionAsync("""
                marker => {
                  const terminal = window.ilreplTerminal;
                  const buffer = terminal.buffer.active;
                  const prompt = buffer.getLine(buffer.baseY + terminal.rows - 2)?.translateToString(true).trimEnd() ?? '';
                  const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
                  return prompt.endsWith(`> .reset // ${marker}`) && window.ilreplControlFlowWriteCount > 0
                    && !status.includes('editing ')
                    && !status.includes('updating') && !status.includes('sending') && !status.includes('cancelling')
                    && !status.includes('Ctrl+C cancels');
                }
                """, marker, new() { PollingInterval = 16, Timeout = 30_000 });
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "reset was not ready to submit: " + await BrowserWaitStateAsync(page), exception);
        }
    }

    private static async Task RunCorpusCellAsync(IPage page, string source, int expected)
    {
        await PasteAsync(page, source);
        await ArmSubmissionOutputAsync(page);
        await SendTerminalInputAsync(page, "\r");
        try
        {
            await page.WaitForFunctionAsync("""
                expected => {
                  const terminal = window.ilreplTerminal;
                  const buffer = terminal.buffer.active;
                  const prompt = buffer.getLine(buffer.baseY + terminal.rows - 2)?.translateToString(true).trim() ?? '';
                  const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
                  const found = Array.from({ length: buffer.length }, (_, row) =>
                    buffer.getLine(row)?.translateToString(true) ?? '').some(line => line.includes(expected));
                  return found && /^il\[\d+\]>$/.test(prompt)
                    && !status.includes('editing ') && !status.includes('updating') && !status.includes('sending')
                    && !status.includes('cancelling') && !status.includes('Ctrl+C cancels');
                }
                """, $"= {expected} : int32", new() { PollingInterval = 16, Timeout = 30_000 });
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"the corpus cell did not return {expected}: {await BrowserWaitStateAsync(page)}", exception);
        }
    }

    private static Task FocusPromptAsync(IPage page) => page.Locator(".xterm-helper-textarea").FocusAsync();

    private static async Task SendTerminalInputAsync(IPage page, string input)
    {
        await page.EvaluateAsync("input => window.ilreplTerminal.input(input, true)", input);
    }

    private static async Task ResetCompletedAsync(IPage page, string marker)
    {
        try
        {
            await page.WaitForFunctionAsync("""
                marker => {
                  const terminal = window.ilreplTerminal;
                  const buffer = terminal.buffer.active;
                  const prompt = buffer.getLine(buffer.baseY + terminal.rows - 2)?.translateToString(true).trim() ?? '';
                  const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
                  const lines = Array.from({ length: buffer.length }, (_, row) =>
                    buffer.getLine(row)?.translateToString(true) ?? '');
                  const reset = lines.findLastIndex(line => line.includes(marker));
                  const cleared = lines.slice(reset + 1)
                    .some(line => line.includes('cell, declarations, methods, and types cleared'));
                  return reset >= 0 && cleared && /^il\[\d+\]>$/.test(prompt)
                    && window.ilreplControlFlowWriteCount > 0
                    && !status.includes('editing ')
                    && !status.includes('updating') && !status.includes('sending') && !status.includes('cancelling')
                    && !status.includes('Ctrl+C cancels');
                }
                """, marker, new() { PollingInterval = 16, Timeout = 30_000 });
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
