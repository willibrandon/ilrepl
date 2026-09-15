using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser copies discover sibling types that their original method reaches solely through reflection names.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Real loaded sibling metadata executes unchanged, revises through hydrated source, compares in workers, and survives export.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="api">The actual BCL lookup family.</param>
    /// <param name="shape">The sibling type-name shape.</param>
    /// <param name="arity">The BCL overload parameter count.</param>
    /// <param name="ignoreCase">Whether the name requires case-insensitive matching.</param>
    /// <param name="flow">The route taken by the name to its lookup.</param>
    /// <returns>The completed original, revision, isolated worker, and export assertions.</returns>
    [TestMethod]
    [DataRow("chromium", "type", "plain", 1, false, "literal")]
    [DataRow("webkit", "type", "plain", 1, false, "literal")]
    [DataRow("chromium", "type", "component", 3, true, "return")]
    [DataRow("webkit", "type", "component", 3, true, "return")]
    [DataRow("chromium", "type", "array", 3, true, "concat")]
    [DataRow("webkit", "type", "array", 3, true, "concat")]
    [DataRow("chromium", "assembly", "nested", 3, true, "argument")]
    [DataRow("webkit", "assembly", "nested", 3, true, "argument")]
    [DataRow("chromium", "module", "matrix", 3, true, "local")]
    [DataRow("webkit", "module", "matrix", 3, true, "local")]
    [DataRow("chromium", "module", "generic", 3, false, "identity")]
    [DataRow("webkit", "module", "generic", 3, false, "identity")]
    [DataRow("chromium", "assembly-create", "plain", 1, false, "literal")]
    [DataRow("webkit", "assembly-create", "plain", 1, false, "literal")]
    [DataRow("chromium", "assembly-create", "generic", 7, true, "return")]
    [DataRow("webkit", "assembly-create", "generic", 7, true, "return")]
    [DataRow("chromium", "activator", "plain", 2, false, "literal")]
    [DataRow("webkit", "activator", "plain", 2, false, "literal")]
    [DataRow("chromium", "activator", "nested", 8, true, "argument")]
    [DataRow("webkit", "activator", "nested", 8, true, "argument")]
    [DataRow("chromium", "activator-from", "plain", 2, false, "literal")]
    [DataRow("webkit", "activator-from", "plain", 2, false, "literal")]
    [DataRow("chromium", "activator-from", "generic", 8, true, "return")]
    [DataRow("webkit", "activator-from", "generic", 8, true, "return")]
    [DataRow("chromium", "type", "plain", 1, false, "runtime")]
    [DataRow("webkit", "type", "plain", 1, false, "runtime")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditDiscoversNameOnlySibling(string browser, string api, string shape, int arity,
        bool ignoreCase, string flow)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(SiblingTypeLookupFixture.Create(api, shape, arity, ignoreCase,
            internalType: true, qualified: true, flow, "sibling-source.dll"));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/sibling-input\"\n"
            + "call class DirectoryInfo Directory::CreateDirectory(string)\npop\n"
            + "ldstr \"/tmp/sibling-input\"\ncall void Directory::SetCurrentDirectory(string)\n"
            + "ldstr \"sibling-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/sibling-input/sibling-source.dll");
        await ExpectCompletionAsync(page, "types)");
        var signature = "int32 Lookup.Owner::Read(" + (flow == "runtime" ? "string" : "") + ")";
        var argument = flow == "runtime" ? "ldstr \"Lookup.Sibling\"\n" : "";
        var comparison = flow == "runtime" ? ".compare Copy (\"Lookup.Sibling\")" : ".compare Copy ()";
        await RunCorpusCellAsync(page, argument + "call " + signature + "\nret", 42);
        await TypeLineAsync(page, ".edit " + signature + " as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, argument + "call Copy\nret", 42);
        var history = await StoredHistoryAsync(page);
        var submitted = history.Last(entry => entry.StartsWith(".edit ", StringComparison.Ordinal));
        Assert.Contains(".method", submitted);
        Assert.Contains("ret", submitted);
        if (api == "activator-from")
        {
            await SubmitEditSourceAsync(page, ".method int32 SiblingScenario() {\n"
                + "ldstr \"sibling-source.dll\"\nldstr \"" + image + "\"\n"
                + "call uint8[] Convert::FromBase64String(string)\n"
                + "call void File::WriteAllBytes(string, uint8[])\ncall Copy\nret\n}", "end of method SiblingScenario");
            comparison = ".compare Copy using SiblingScenario";
        }
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, comparison);
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            AssertSiblingComparisonSide(side, "42");
        }
        await ExpectComparisonTextAsync(page, "Copy: match");
        var source = ".edit Copy " + submitted[submitted.IndexOf('{')..];
        source = source.Insert(source.LastIndexOf("ret", StringComparison.Ordinal), "ldc.i4.1\nadd\n");
        await SubmitEditSourceAsync(page, source, "edit Copy committed as revision 2");
        await RunCorpusCellAsync(page, argument + "call Copy\nret", 43);
        await RunCorpusCellAsync(page, argument + "call " + signature + "\nret", 42);
        await TypeLineAsync(page, comparison);
        foreach (var (index, expected) in new[] { (2, "42"), (3, "43") })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            AssertSiblingComparisonSide(side, expected);
        }
        await ExpectComparisonTextAsync(page, "Copy: different");
        if (flow == "runtime") await TypeLineAsync(page, "ldstr \"Lookup.Sibling\"");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/sibling-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/sibling-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/sibling-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [sibling-copy]IlRepl.Cell::Run()\nret", 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertSiblingComparisonSide(JsonElement side, string expected)
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
