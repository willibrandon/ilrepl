using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Jump completion excludes incompatible overloads before accepting and running the matching target.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_JumpCompletion_FiltersSignatures(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, ".method int32 Bridge(int32 value) {\njmp Math::Abs");
        await ExpectCompletionAsync(page, "❯ Abs(int32)");
        await ExpectCompletionAsync(page, "members 1/1");
        await page.Keyboard.PressAsync("Tab");
        await PasteAsync(page, "\n}\nldc.i4.s -7\ncall Bridge\nret");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 7 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Jump targets and nested types beneath generic owners complete and execute in the browser.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_JumpAndNestedTypeCompletions_Bind(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, ".method int32 Bridge() {\njmp int32 Environment::get_TickC");
        await ExpectCompletionAsync(page, "❯ get_TickCount()");
        await page.Keyboard.PressAsync("Tab");
        await PasteAsync(page, "\n}\ncall Bridge\npop\nldc.i4.7\nret");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 7 : int32");
        await TypeLineAsync(page, ".clear");
        await PasteAsync(page, ".locals init (valuetype System.Collections.Generic.Dictionary`2/Enum");
        await CompletionAtCaretAsync(page,
            "il[3]> .locals init (valuetype System.Collections.Generic.Dictionary`2/Enum", "❯ Enumerator<!TKey, !TValue>");
        await page.Keyboard.PressAsync("Tab");
        await TypeLineAsync(page, "int32, string> value)");
        await TypeLineAsync(page, "ldloc.0");
        await TypeLineAsync(page, "pop");
        await TypeLineAsync(page, "ldc.i4.8");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 8 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// An explicitly typed generic arity still offers a constructed type that binds and executes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ExplicitGenericArity_Completes(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, ".locals init (class List`1");
        await CompletionAtCaretAsync(page, "il[1]> .locals init (class List`1", "❯ List<!T>");
        await page.Keyboard.PressAsync("Tab");
        await TypeLineAsync(page, "int32> items)");
        await TypeLineAsync(page, "ldloc.0");
        await TypeLineAsync(page, "pop");
        await TypeLineAsync(page, "ldc.i4 9");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 9 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Void remains a usable pointer-array element and type token after array-element completion filtering.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_VoidPointerArrayCompletion_Binds(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, "ldc.i4.0");
        await page.Keyboard.TypeAsync("newarr vo*");
        await page.Keyboard.PressAsync("ArrowLeft");
        await CompletionAtCaretAsync(page, "il[1]> newarr vo", "❯ void");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ldlen");
        await TypeLineAsync(page, "conv.i4");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 0 : int32");
        await page.Keyboard.TypeAsync("ldtoken vo");
        await CompletionAtCaretAsync(page, "il[2]> ldtoken vo", "❯ void");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= typeof(void)");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Array member completion and a label preceding its own branch bind in the browser engine.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ArrayAndCurrentLabelCompletions_Bind(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, ".load /samples/Greeter.dll");
        await ExpectCompletionAsync(page, "loaded Greeter");
        await TypeLineAsync(page, "ldnull");
        await page.Keyboard.TypeAsync("call Greeter.Hello::AcceptMatrix<int32>");
        await CompletionAtCaretAsync(page, "il[1]> call Greeter.Hello::AcceptMatrix<int32>", "signatures 1/1");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 9 : int32");
        await TypeLineAsync(page, ".clear");
        await PasteAsync(page, ".method void Spin() {\nLOOP: br LO");
        await ExpectCompletionAsync(page, "❯ LOOP");
        await page.Keyboard.PressAsync("Tab");
        await PasteAsync(page, "\n}");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "end of method Spin");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A completed unmanaged function-pointer parameter binds in Mono and qualified typo suggestions recover correctly.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_FunctionPointerCompletion_AndQualifiedSuggestions_Bind(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, ".load /samples/Greeter.dll");
        await ExpectCompletionAsync(page, "loaded Greeter");
        await TypeLineAsync(page, "ldc.i4.0");
        await TypeLineAsync(page, "conv.u");
        await page.Keyboard.TypeAsync("call Greeter.Hello::AcceptCd");
        await CompletionAtCaretAsync(page, "il[1]> call Greeter.Hello::AcceptCd", "❯ AcceptCdecl(");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 7 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
        await TypeLineAsync(page, ".clear");
        await PasteAsync(page, ".method int32 CheckPointer() {\nldc.i4.0\nconv.u\n"
            + "call Greeter.Hello::AcceptCdecl\nldc.i4.4\nadd\nret\n}\ncall CheckPointer\nret");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 11 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
        await TypeLineAsync(page, ".clear");
        await TypeLineAsync(page, "ldtoken Systm.Console");
        await ExpectCompletionAsync(page, "did you mean 'Console'");
        await EmptyPromptAsync(page);
        await TypeLineAsync(page, "ldtoken [System.Runtime]System.Text.StringBuildr");
        await ExpectCompletionAsync(page, "'StringBuilder'?");
        await EmptyPromptAsync(page);
        await TypeLineAsync(page, "ldtoken System.Environmnt/SpecialFolder");
        await ExpectCompletionAsync(page, "'SpecialFolder'?");
        await EmptyPromptAsync(page);
        await TypeLineAsync(page, "ldtoken SpecialFolder");
        await TypeLineAsync(page, "call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "SpecialFolder) : RuntimeType");
    }

    /// <summary>
    /// Refusing a replacement that removes a required nested type preserves its inspectable and executable definition.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_RemovedDependency_RefusesWithoutLosingTheAcceptedType(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, ".class public Outer {\n.class nested public Inner { }\n}\n"
            + ".class public Holder {\n.field public class Outer/Inner Value\n}");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "end of class Holder");
        await PasteAsync(page, ".class public Outer {\n}\n.dis Outer/Inn");
        await ExpectCompletionAsync(page, "❯ Inner");
        await page.Keyboard.PressAsync("Control+c");
        await TypeLineAsync(page, ".class public Outer {");
        await TypeLineAsync(page, "}");
        await ExpectCompletionAsync(page, "cannot redefine");
        await page.Keyboard.PressAsync("Control+c");
        await TypeLineAsync(page, ".clear");
        await page.Keyboard.TypeAsync("ldtoken Outer/Inn");
        await ExpectCompletionAsync(page, "❯ Inner");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= typeof(Outer/Inner)");
    }

    /// <summary>
    /// Private members complete in their own unsent body and inspection can list them after the class is accepted.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_PrivateCompletion_UsesTheBodyAndInspectionContext(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, ".class public Vault {\n.method private static int32 Secret() {\nldc.i4.7\nret\n}\n"
            + ".method public static int32 Read() {\ncall Vault::Sec");
        await ExpectCompletionAsync(page, "❯ Secret()");
        await page.Keyboard.PressAsync("Tab");
        await PasteAsync(page, "\nret\n}\n}");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "end of class Vault");
        await TypeLineAsync(page, "call Vault::Read()");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 7 : int32");
        await page.Keyboard.TypeAsync(".dis Vault::Sec");
        await ExpectCompletionAsync(page, "❯ Secret()");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "0000 ldc.i4.7");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A loaded protected field completes inside a derived type and its accepted getter executes correctly.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_FamilyCompletion_UsesTheDerivedBody(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, ".load /samples/Greeter.dll");
        await ExpectCompletionAsync(page, "loaded Greeter");
        await PasteAsync(page, ".class public Ledger extends Greeter.Account {\n.method public instance int32 Read() {\n"
            + "ldarg.0\nldfld Greeter.Account::Bal");
        await ExpectCompletionAsync(page, "fields 1/1");
        await page.Keyboard.PressAsync("Tab");
        await PasteAsync(page, "\nret\n}\n.method public instance void .ctor() {\nldarg.0\n"
            + "call instance void Greeter.Account::.ctor()\nret\n}\n}");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "end of class Ledger");
        await TypeLineAsync(page, "newobj Ledger::.ctor()");
        await TypeLineAsync(page, "dup");
        await TypeLineAsync(page, "ldc.i4.5");
        await TypeLineAsync(page, "callvirt instance void Greeter.Account::Deposit(int32)");
        await TypeLineAsync(page, "callvirt instance int32 Ledger::Read()");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 5 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Case-distinct loaded members insert quoted identifiers and typo diagnostics retain their intended candidates.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_LoadedCompletion_QuotesAndSuggests(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await TypeLineAsync(page, ".load /samples/Greeter.dll");
        await ExpectCompletionAsync(page, "loaded Greeter");
        await TypeLineAsync(page, "ldc.i4.3");
        await TypeLineAsync(page, "ldc.i4.4");
        await page.Keyboard.TypeAsync("call Greeter.Hello::ad");
        await ExpectCompletionAsync(page, "❯ add(int32, int32)");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("❯ add(int32, int32)");
        Assert.Contains("::'add'(int32, int32)", (await BufferRowsAsync(page))[^2]);
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 7 : int32");
        await TypeLineAsync(page, ".clear");
        await TypeLineAsync(page, "call Console::WritLne(string)");
        await ExpectCompletionAsync(page, "did you mean");
        await ExpectCompletionAsync(page, "WriteLine");
        await TypeLineAsync(page, "ldtoken Strng");
        await ExpectCompletionAsync(page, "did you mean 'string'");
    }

    /// <summary>
    /// An unsent replacement supplies new member identities and publishes the method chosen by completion.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_UnsentReplacement_CompletesTheNewMember(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, ".class public Replaced {\n.method public static int32 Old() {\nldc.i4.1\nret\n}\n}");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "end of class Replaced");
        await PasteAsync(page, ".class public Replaced {\n.method public static int32 New() {\nldc.i4.s 42\nret\n}\n}\n"
            + "call Replaced::Ne");
        await ExpectCompletionAsync(page, "members 1/1");
        await page.Keyboard.PressAsync("Tab");
        await PasteAsync(page, "\nret");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 42 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Completion inside a replacement binds the rebuilt dependency and the published cycle executes its new generation.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_CyclicReplacement_CompletesAgainstTheNewGeneration(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, ".class public A {\n.method public static int32 Value() {\nldc.i4.1\nret\n}\n}\n"
            + ".class public B {\n.method public static int32 Twice() {\ncall A::Value()\nldc.i4.2\nmul\nret\n}\n}");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "end of class B");
        await PasteAsync(page, ".class public A {\n.method public static int32 Value() {\nldc.i4.s 10\nret\n}\n"
            + ".method public static int32 Four() {\ncall B::Twi");
        await ExpectCompletionAsync(page, "❯ Twice()");
        await page.Keyboard.PressAsync("Tab");
        await PasteAsync(page, "\nldc.i4.2\nmul\nret\n}\n}\ncall A::Four()\nret");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 40 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// An accessible compiler-shaped metadata name is found by substring and its quoted member spelling binds exactly.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_LoadedCompilerName_CompletesItsQuotedReference(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await PasteAsync(page, ".class public '<>c__DisplayClassProbe' {\n.method public static int32 Value() {\n"
            + "ldc.i4.7\nret\n}\n}");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "end of class <>c__DisplayClassProbe");
        await TypeLineAsync(page, ".save /tmp/completion-compiler-name.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/completion-compiler-name.dll");
        await TypeLineAsync(page, ".reset");
        await TypeLineAsync(page, ".load /tmp/completion-compiler-name.dll");
        await ExpectCompletionAsync(page, "public types)");
        await page.Keyboard.TypeAsync("call DisplayClassProbe");
        await ExpectCompletionAsync(page, "❯ '<>c__DisplayClassProbe'");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync("Val");
        await page.WaitForFunctionAsync("""
            column => {
              const terminal = window.ilreplTerminal;
              const line = terminal.buffer.active.getLine(terminal.rows - 2);
              return line?.getCell(column)?.getBgColor() === 0x61afef;
            }
            """, "il[2]> call '<>c__DisplayClassProbe'::Val".Length);
        await ExpectCompletionAsync(page, "❯ Value()");
        await ExpectCompletionAsync(page, "members 1/1");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("❯ Value()");
        Assert.Contains("'<>c__DisplayClassProbe'::Value()", (await BufferRowsAsync(page))[^2]);
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 7 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A value-type constraint excludes string and recovers to a valid argument without losing its generic owner.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_GenericConstraint_RejectsThenRecoversTheArgument(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var terminal = page.Locator("#terminal");
        await TypeLineAsync(page, ".load /samples/Greeter.dll");
        await ExpectCompletionAsync(page, "loaded Greeter");
        await page.Keyboard.TypeAsync("call Greeter.Generic::Constrained");
        await CompletionAtCaretAsync(page, "il[1]> call Greeter.Generic::Constrained", "members 1/1");
        await page.Keyboard.PressAsync("Tab");
        await ExpectCompletionAsync(page, "type argument 1 of 1 (T)");
        await page.Keyboard.TypeAsync("string>");
        await ExpectCompletionAsync(page, "Constrained<string>");
        await Assertions.Expect(terminal).Not.ToContainTextAsync("signatures 1/1");
        for (var index = 0; index < "string>".Length; index++)
        {
            await page.Keyboard.PressAsync("Backspace");
        }

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(terminal).Not.ToContainTextAsync("type argument 1 of 1 (T)");
        await page.WaitForFunctionAsync("""
            () => {
              const terminal = window.ilreplTerminal;
              return terminal.buffer.active.getLine(terminal.rows - 2)?.translateToString(true).trim()
                === 'il[1]> call Generic::Constrained<';
            }
            """);
        Assert.AreEqual("il[1]> call Generic::Constrained<", (await BufferRowsAsync(page))[^2].Trim());
        await page.Keyboard.TypeAsync("int32");
        await ExpectCompletionAsync(page, "❯ int32");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">");
        await ExpectCompletionAsync(page, "signatures 1/1");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 0 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    private static Task<IJSHandle> PromptAtCaretAsync(IPage page, string prompt) => page.WaitForFunctionAsync("""
        prompt => {
          const terminal = window.ilreplTerminal;
          const row = terminal.buffer.active.getLine(terminal.rows - 2);
          return row?.translateToString(true).trim() === prompt && row.getCell(prompt.length)?.getBgColor() === 0x61afef;
        }
        """, prompt, new() { PollingInterval = 16, Timeout = 30_000 });

    private static Task<IJSHandle> CompletionAtCaretAsync(IPage page, string prompt, string choice) => page.WaitForFunctionAsync("""
        ({ prompt, choice }) => {
          const terminal = window.ilreplTerminal;
          const row = terminal.buffer.active.getLine(terminal.rows - 2);
          if (row?.getCell(prompt.length)?.getBgColor() !== 0x61afef) return false;
          const rows = Array.from({ length: terminal.rows }, (_, index) =>
            terminal.buffer.active.getLine(index)?.translateToString(true) ?? '');
          return rows.some(line => line.includes(choice)) && !rows.some(line => line.includes('updating '));
        }
        """, new { prompt, choice }, new() { PollingInterval = 16, Timeout = 30_000 });

    private static Task<IJSHandle> EmptyPromptAsync(IPage page) => page.WaitForFunctionAsync("""
        () => {
          const terminal = window.ilreplTerminal;
          const row = terminal.buffer.active.getLine(terminal.rows - 2)?.translateToString(true).trim() ?? '';
          const status = terminal.buffer.active.getLine(terminal.rows - 1)?.translateToString(true) ?? '';
          return /^il\[\d+\]>$/.test(row) && !status.includes('sending ');
        }
        """);
}
