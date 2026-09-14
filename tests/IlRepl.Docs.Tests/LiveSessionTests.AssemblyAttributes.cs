using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Assembly and module attribute inspection receives actionable browser preflight errors and preserves the real original.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Each reflection dispatch form supports original execution, blocked-draft correction, fresh comparison and independent export.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="target">Assembly or Module.</param>
    /// <param name="dispatch">The instance, static helper, generic extension, data, or interface API family.</param>
    /// <returns>The completed original, preflight, recovery, worker, and export assertions.</returns>
    [TestMethod]
    [DataRow("chromium", "Assembly", "instance")]
    [DataRow("webkit", "Assembly", "instance")]
    [DataRow("chromium", "Module", "instance")]
    [DataRow("webkit", "Module", "instance")]
    [DataRow("chromium", "Assembly", "attribute")]
    [DataRow("webkit", "Assembly", "attribute")]
    [DataRow("chromium", "Module", "attribute")]
    [DataRow("webkit", "Module", "attribute")]
    [DataRow("chromium", "Assembly", "extensions")]
    [DataRow("webkit", "Assembly", "extensions")]
    [DataRow("chromium", "Module", "extensions")]
    [DataRow("webkit", "Module", "extensions")]
    [DataRow("chromium", "Assembly", "data")]
    [DataRow("webkit", "Assembly", "data")]
    [DataRow("chromium", "Module", "data")]
    [DataRow("webkit", "Module", "data")]
    [DataRow("chromium", "Assembly", "provider")]
    [DataRow("webkit", "Assembly", "provider")]
    [DataRow("chromium", "Module", "provider")]
    [DataRow("webkit", "Module", "provider")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditRejectsAssemblyAttributeInspection(string browser, string target, string dispatch)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var api = AssemblyAttributeFixture.Apis(target, dispatch).First(method => dispatch != "extensions" || method.IsGenericMethod);
        var image = Convert.ToBase64String(AssemblyAttributeFixture.Create(target, api));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/attributes.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/attributes.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 AttributeInspection.Owner::Read()\nret", 42);
        await TypeLineAsync(page, ".edit int32 AttributeInspection.Owner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        await ExpectComparisonTextAsync(page, "original metadata");
        var diagnostic = await BufferTextAsync(page);
        var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("assembly and module attribute inspection cannot reproduce the original metadata", text);
        await PromptContainsAsync(page, "}");
        await ClearPromptAsync(page);
        await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 42\nret\n}\n}",
            "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("42", side.GetProperty("result").GetProperty("value").GetString());
            Assert.AreEqual(1, side.GetProperty("invocations").GetArrayLength());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/attribute-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/attribute-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/attribute-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [attribute-copy]IlRepl.Cell::Run()\nret", 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
