using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser reflection constructs real copied instances and retains downstream method provenance in fresh workers and exports.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// ConstructorInfo and ConstructorInvoker preserve private, generic, and helper-selected construction without preflight execution.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="lookup">The constructor lookup API.</param>
    /// <param name="argument">Whether construction uses one integer.</param>
    /// <param name="privateConstructor">Whether the constructor is private.</param>
    /// <param name="flow">The receiver provenance.</param>
    /// <param name="invoker">Whether to allocate through ConstructorInvoker.</param>
    /// <param name="generic">Whether the selected owner is generic.</param>
    /// <returns>The completed actual runtime, worker, and export assertions.</returns>
    [TestMethod]
    [DataRow("chromium", "one", false, false, "direct", false, false)]
    [DataRow("webkit", "one", false, false, "direct", false, false)]
    [DataRow("chromium", "two", true, true, "local", false, false)]
    [DataRow("webkit", "two", true, true, "local", false, false)]
    [DataRow("chromium", "five", true, true, "return", false, false)]
    [DataRow("webkit", "five", true, true, "return", false, false)]
    [DataRow("chromium", "index", false, false, "direct", false, false)]
    [DataRow("webkit", "index", false, false, "direct", false, false)]
    [DataRow("chromium", "declared", true, true, "return", false, false)]
    [DataRow("webkit", "declared", true, true, "return", false, false)]
    [DataRow("chromium", "as-type", true, false, "direct", false, false)]
    [DataRow("webkit", "as-type", true, false, "direct", false, false)]
    [DataRow("chromium", "one", false, false, "direct", true, false)]
    [DataRow("webkit", "one", false, false, "direct", true, false)]
    [DataRow("chromium", "four", true, true, "local", true, true)]
    [DataRow("webkit", "four", true, true, "local", true, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesReflectedConstructors(string browser, string lookup, bool argument,
        bool privateConstructor, string flow, bool invoker, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var source = ConstructorReflectionExamples.Source(lookup, argument, privateConstructor, flow, invoker, generic);
        var reference = ConstructorReflectionExamples.Reference(generic);
        await SubmitEditSourceAsync(page, source + "\n.edit " + reference + " as Copy {\n"
            + ConstructorReflectionExamples.Method(lookup, argument, privateConstructor, flow, invoker, generic) + "\n}",
            "edit Copy committed as revision 1");
        var originalOwner = generic ? "class ConstructorOwner<int32>" : "ConstructorOwner";
        var copiedOwner = generic ? "class IlRepl.Edits.Copy.Owner<int32>" : "IlRepl.Edits.Copy.Owner";
        await RunCorpusCellAsync(page, "ldsfld int32 " + originalOwner + "::Constructions\nret", 0);
        await RunCorpusCellAsync(page, "ldsfld int32 " + copiedOwner + "::Constructions\nret", 0);
        await RunCorpusCellAsync(page, "call " + reference + "\nret", 42);
        await RunCorpusCellAsync(page, "call Copy\nret", 42);
        await RunCorpusCellAsync(page, "ldsfld int32 " + originalOwner + "::Constructions\nret", 1);
        await RunCorpusCellAsync(page, "ldsfld int32 " + copiedOwner + "::Constructions\nret", 1);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 }) AssertConstructorComparisonSide(await WaitForComparisonResultAsync(parent, index), "42");
        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".edit Copy {\n"
            + ConstructorReflectionExamples.Method(lookup, argument, privateConstructor, flow, invoker, generic, edited: true) + "\n}",
            "edit Copy committed as revision 2");
        await RunCorpusCellAsync(page, "ldsfld int32 " + originalOwner + "::Constructions\nret", 1);
        await RunCorpusCellAsync(page, "ldsfld int32 " + copiedOwner + "::Constructions\nret", 0);
        await RunCorpusCellAsync(page, "call Copy\nret", 43);
        await TypeLineAsync(page, ".compare Copy ()");
        AssertConstructorComparisonSide(await WaitForComparisonResultAsync(parent, 2), "42");
        AssertConstructorComparisonSide(await WaitForComparisonResultAsync(parent, 3), "43");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/constructor-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/constructor-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/constructor-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [constructor-copy]IlRepl.Cell::Run()\nret", 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// TypeInitializer lookup invokes the real static constructor through MethodBase.Invoke in each independent browser worker.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed original, copied, and revised worker assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesReflectedTypeInitializer(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ConstructorReflectionExamples.InitializerSource(), "end of class ConstructorOwner");
        await RunCorpusCellAsync(page, "call int32 ConstructorOwner::Read()\nret", 42);
        await TypeLineAsync(page, ".edit int32 ConstructorOwner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call Copy\nret", 42);
        var history = await StoredHistoryAsync(page);
        var submitted = history.Last(entry => entry.StartsWith(".edit ", StringComparison.Ordinal));
        var source = ".edit Copy " + submitted[submitted.IndexOf('{')..];
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        AssertConstructorComparisonSide(await WaitForComparisonResultAsync(parent, 0), "42");
        AssertConstructorComparisonSide(await WaitForComparisonResultAsync(parent, 1), "42");
        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, source.Insert(source.LastIndexOf("ret", StringComparison.Ordinal), "ldc.i4.1\nadd\n"),
            "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        AssertConstructorComparisonSide(await WaitForComparisonResultAsync(parent, 2), "42");
        AssertConstructorComparisonSide(await WaitForComparisonResultAsync(parent, 3), "43");
        await ExpectComparisonTextAsync(page, "Copy: different");
    }

    /// <summary>
    /// An unknown ConstructorInfo input preserves its original executable and an editable draft that can be corrected.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="invoker">Whether the unknown constructor reaches ConstructorInvoker.Create.</param>
    /// <returns>The completed live original, refusal, and correction assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditRejectsUnknownConstructor(string browser, bool invoker)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ConstructorReflectionExamples.Source("one", true, false, "unknown", invoker),
            "end of class ConstructorOwner");
        const string inputs = "ldtoken ConstructorOwner\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n"
            + "ldc.i4.1\nnewarr class Type\ndup\nldc.i4.0\nldtoken int32\n"
            + "call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\nstelem.ref\n"
            + "callvirt instance class ConstructorInfo Type::GetConstructor(class Type[])\n";
        var reference = ConstructorReflectionExamples.Reference(flow: "unknown");
        await RunCorpusCellAsync(page, inputs + "call " + reference + "\nret", 42);
        await TypeLineAsync(page, ".edit " + reference + " as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        await ExpectComparisonTextAsync(page, "supported");
        var text = string.Join(" ", (await BufferTextAsync(page)).Replace('│', ' ').Replace('▉', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("indirect reflection cannot prove a supported target", text);
        await PromptContainsAsync(page, "}");
        await ClearPromptAsync(page);
        await RunCorpusCellAsync(page, "ldsfld int32 ConstructorOwner::Constructions\nret", 1);
        await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read(class ConstructorInfo constructor) {\n"
            + "ldc.i4.s 42\nret\n}\n}", "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, inputs + "call Copy\nret", 42);
        await RunCorpusCellAsync(page, "ldsfld int32 ConstructorOwner::Constructions\nret", 1);
    }

    private static void AssertConstructorComparisonSide(JsonElement side, string expected)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var result), json);
        Assert.AreEqual(JsonValueKind.Object, result.ValueKind, json);
        Assert.AreEqual(expected, result.GetProperty("value").GetString(), json);
        Assert.AreEqual(1, side.GetProperty("invocations").GetArrayLength(), json);
    }
}
