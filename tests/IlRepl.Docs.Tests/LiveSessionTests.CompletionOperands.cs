using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Earlier declaration types remain completable while a method trailer or field initializer is unfinished.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="field">Whether the unfinished suffix is a field initializer.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_UnfinishedDeclarationSuffix_CompletesEarlierType(string browser, bool field)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var prefix = field ? ".field public static literal int3" : ".method int32 M(List<int3";
        var suffix = field ? " Value = int32(" : "> value) cil man";
        await PasteAsync(page, (field ? ".class public Host {\n" : "") + prefix + suffix);
        for (var i = 0; i < suffix.Length; i++)
        {
            await page.Keyboard.PressAsync("ArrowLeft");
        }

        await CompletionAtCaretAsync(page, (field ? "  ...> " : "il[1]> ") + prefix, "❯ int32");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("End");
        await PromptContainsAsync(page, prefix + "2" + suffix);
        await PasteAsync(page, field
            ? "7)\n}\nldtoken field Host::Value\ncall System.Reflection.FieldInfo::GetFieldFromHandle(RuntimeFieldHandle)\n"
                + "callvirt object System.Reflection.FieldInfo::GetRawConstantValue()\nret"
            : "aged {\nldc.i4.7\nret\n}\nldnull\ncall int32 M(List<int32>)\nret");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 7 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// An unfinished interface clause permits completing the earlier base type and then running the finished class.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_UnfinishedInheritance_CompletesEarlierArgument(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        const string prefix = ".class public Derived extends List<int3";
        const string suffix = "> implements";
        await PasteAsync(page, prefix + suffix);
        for (var i = 0; i < suffix.Length; i++)
        {
            await page.Keyboard.PressAsync("ArrowLeft");
        }

        await CompletionAtCaretAsync(page, "il[1]> " + prefix, "❯ int32");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("End");
        await PromptAtCaretAsync(page, "il[1]> .class public Derived extends List<int32> implements");
        await PasteAsync(page, " IEnumerable<int32> {\n.method public instance void .ctor() {\nldarg.0\n"
            + "call instance void List<int32>::.ctor()\nret\n}\n}\nnewobj Derived::.ctor()\n"
            + "callvirt List<int32>::get_Count()\nret");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 0 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Completing a generic attribute argument preserves an attribute that the browser runtime can inspect.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_GenericAttribute_CompletesAndBinds(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        const string prefix = ".custom instance void Mark<int3";
        const string suffix = ">::.ctor()";
        await PasteAsync(page, $$"""
            .class public Mark<T> extends System.Attribute {
            .method public instance void .ctor() {
            ldarg.0
            call instance void System.Attribute::.ctor()
            ret
            }
            }
            .class public Host {
            {{prefix}}{{suffix}}
            """);
        for (var i = 0; i < suffix.Length; i++)
        {
            await page.Keyboard.PressAsync("ArrowLeft");
        }

        await CompletionAtCaretAsync(page, "  ...> " + prefix, "❯ int32");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("End");
        await PromptContainsAsync(page, ".custom instance void Mark<int32>::.ctor()");
        await PasteAsync(page, "\n}\nldtoken Host\ncall Type::GetTypeFromHandle(RuntimeTypeHandle)\n"
            + "ldtoken Mark<int32>\ncall Type::GetTypeFromHandle(RuntimeTypeHandle)\n"
            + "call bool Attribute::IsDefined(System.Reflection.MemberInfo, Type)\nret");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= true : bool");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A valid nested type argument remains completable inside a complete generic call and executes after acceptance.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_NestedGenericOperand_CompletesAndRuns(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        const string prefix = "call Array::Empty<List<Nullable<int3";
        const string suffix = ">>>()";
        await PasteAsync(page, prefix + suffix);
        for (var i = 0; i < suffix.Length; i++)
        {
            await page.Keyboard.PressAsync("ArrowLeft");
        }

        await CompletionAtCaretAsync(page, "il[1]> " + prefix, "❯ int32");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("End");
        await PromptAtCaretAsync(page, "il[1]> call Array::Empty<List<Nullable<int32>>>()");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ldlen");
        await TypeLineAsync(page, "conv.i4");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 0 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// An event handler completion disappears for an array suffix and returns when the delegate type is restored.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the edited component is the delegate's generic argument.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EventHandlerCompletion_RejectsArraySuffix(string browser, bool generic)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var handler = generic ? "Action<int32>" : "Action";
        var prefix = generic ? ".event System.Action<int3" : ".event System.Act";
        var choice = generic ? "❯ int32" : "❯ Action ";
        await PasteAsync(page, $$"""
            .class public EventHost {
            .method public static specialname void add_Changed({{handler}} value) {
            ret
            }
            .method public static specialname void remove_Changed({{handler}} value) {
            ret
            }
            {{prefix}}
            """);
        await CompletionAtCaretAsync(page, "  ...> " + prefix, choice);
        await page.Keyboard.TypeAsync(generic ? ">[]" : "[]");
        if (generic)
        {
            await page.Keyboard.PressAsync("ArrowLeft");
        }

        await page.Keyboard.PressAsync("ArrowLeft");
        await page.Keyboard.PressAsync("ArrowLeft");
        await PromptContainsAsync(page, prefix + (generic ? ">[]" : "[]"));
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync(choice);
        if (generic)
        {
            await page.Keyboard.PressAsync("ArrowRight");
        }

        await page.Keyboard.PressAsync("Delete");
        await page.Keyboard.PressAsync("Delete");
        if (generic)
        {
            await page.Keyboard.PressAsync("ArrowLeft");
        }

        await CompletionAtCaretAsync(page, "  ...> " + prefix, choice);
        await page.Keyboard.PressAsync("Tab");
        if (generic)
        {
            await page.Keyboard.PressAsync("ArrowRight");
        }

        await PasteAsync(page, $$"""
             Changed {
            .addon void EventHost::add_Changed({{handler}})
            .removeon void EventHost::remove_Changed({{handler}})
            }
            }
            ldnull
            call void EventHost::add_Changed({{handler}})
            ldc.i4.7
            ret
            """);
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "= 7 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A rejected typed-reference generic argument can be replaced by a completed string argument and executed.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="method">Whether the generic owner is a method.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_GenericArgument_RejectsTypedReference(string browser, bool method)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        var prefix = method ? "call Array::Empty<" : "ldtoken List<";
        await page.Keyboard.TypeAsync(prefix + "typedre");
        await PromptAtCaretAsync(page, "il[1]> " + prefix + "typedre");
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("❯ typedref");
        for (var i = 0; i < "typedre".Length; i++)
        {
            await page.Keyboard.PressAsync("Backspace");
        }

        await page.Keyboard.TypeAsync("str");
        await CompletionAtCaretAsync(page, "il[1]> " + prefix + "str", "❯ string");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">");
        if (method)
        {
            await CompletionAtCaretAsync(page, "il[1]> " + prefix + "string>", "signatures 1/1");
            await page.Keyboard.PressAsync("Tab");
        }

        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, method ? "ldlen" : "pop");
        await TypeLineAsync(page, method ? "conv.i4" : "ldc.i4.0");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 0 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// Removing a pointer suffix excludes void from sizeof while restoring it produces an executable operand.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_TypeOperandCompletion_RejectsVoidStorage(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        await page.Keyboard.TypeAsync("sizeof vo*");
        await page.Keyboard.PressAsync("ArrowLeft");
        await CompletionAtCaretAsync(page, "il[1]> sizeof vo", "❯ void");
        await page.Keyboard.PressAsync("Delete");
        await PromptAtCaretAsync(page, "il[1]> sizeof vo");
        await Assertions.Expect(page.Locator("#terminal")).Not.ToContainTextAsync("❯ void");
        await page.Keyboard.TypeAsync("*");
        await page.Keyboard.PressAsync("ArrowLeft");
        await CompletionAtCaretAsync(page, "il[1]> sizeof vo", "❯ void");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("End");
        await PromptAtCaretAsync(page, "il[1]> sizeof void*");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 4 : uint32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A generic starter preserves a comment before its existing bracket and keeps the selected method through execution.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_GenericStarter_PreservesSeparatedBracket(string browser)
    {
        await using var launched = await LaunchAsync(browser);
        await using var context = await NewContextAsync(launched);
        var page = await OpenSessionAsync(context);
        const string gap = " /* < */<";
        await page.Keyboard.TypeAsync("call Array::Em" + gap);
        for (var i = 0; i < gap.Length; i++)
        {
            await page.Keyboard.PressAsync("ArrowLeft");
        }

        await CompletionAtCaretAsync(page, "il[1]> call Array::Em", "members 1/1");
        await page.Keyboard.PressAsync("Tab");
        await PromptAtCaretAsync(page, "il[1]> call Array::Empty" + gap);
        await page.Keyboard.TypeAsync("str");
        await CompletionAtCaretAsync(page, "il[1]> call Array::Empty" + gap + "str", "❯ string");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.TypeAsync(">");
        await CompletionAtCaretAsync(page, "il[1]> call Array::Empty" + gap + "string>", "signatures 1/1");
        await page.Keyboard.PressAsync("Tab");
        await page.Keyboard.PressAsync("Enter");
        await TypeLineAsync(page, "ldlen");
        await TypeLineAsync(page, "conv.i4");
        await TypeLineAsync(page, "ret");
        await ExpectCompletionAsync(page, "= 0 : int32");
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
