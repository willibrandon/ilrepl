using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser source module IDs remain observable while unsupported copied identity reads retain recoverable drafts.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Original module inspection executes before copied reads reject, and an explicit corrected identifier remains executable.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the declaring owner has a type parameter.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditRejectsSourceModuleIdentity(string browser, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ModuleIdentityExamples.Source(generic), "end of class Owner");
        var reference = ModuleIdentityExamples.Reference(generic);
        await RunCorpusCellAsync(page, "call " + reference + "\nldsfld valuetype Guid Guid::Empty\n"
            + "call bool Guid::op_Inequality(valuetype Guid, valuetype Guid)\nldc.i4.s 42\nmul\nret", 42);
        await RunCorpusCellAsync(page, "ldstr \"/tmp/original-mvid.txt\"\ncall " + reference
            + "\nbox Guid\ncallvirt instance string Object::ToString()\ncall void File::WriteAllText(string, string)\n"
            + "ldc.i4.s 44\nret", 44);
        var parent = await ObserveComparisonResultsAsync(page);
        var originalText = await parent.EvaluateAsync<string>("""
            () => globalThis.getDotnetRuntime(0).Module.FS.readFile('/tmp/original-mvid.txt', { encoding: 'utf8' })
            """);
        var original = Guid.Parse(originalText);
        Assert.AreNotEqual(Guid.Empty, original);
        await TypeLineAsync(page, ".edit " + reference + " as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        await ExpectComparisonTextAsync(page, "reproduce");
        var diagnostic = await BufferTextAsync(page);
        var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(AssemblyLocationFixture.Problem("Module", "ModuleVersionId"), text);
        Assert.Contains("ModuleVersionId", text);
        await PromptContainsAsync(page, "}");
        await ClearPromptAsync(page);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + ModuleIdentityExamples.Method(generic, true) + "\n}",
            "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call Copy\nldsfld valuetype Guid Guid::Empty\n"
            + "call bool Guid::op_Equality(valuetype Guid, valuetype Guid)\nldc.i4.s 43\nmul\nret", 43);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null,
                side.GetRawText());
            Assert.AreEqual(Convert.ToHexString((index == 0 ? original : Guid.Empty).ToByteArray()),
                side.GetProperty("result").GetProperty("value").GetString());
            Assert.AreEqual(1, side.GetProperty("invocations").GetArrayLength());
        }
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/module-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/module-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/module-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [module-copy]IlRepl.Cell::Run()\nunbox.any Guid\n"
            + "ldsfld valuetype Guid Guid::Empty\ncall bool Guid::op_Equality(valuetype Guid, valuetype Guid)\nldc.i4.s 43\nmul\nret", 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
