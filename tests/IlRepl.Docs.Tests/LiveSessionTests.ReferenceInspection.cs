using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser reference-table inspection keeps the actual original and recoverable edits consistent across isolated runtimes.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] ReferenceBrowsers = ["chromium", "webkit"];

    /// <summary>
    /// Exercises actual calls, pointers, reflection invocation, and literal or runtime delegate names in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Dispatch)> ReferenceInspectionCases =>
        from browser in ReferenceBrowsers
        from dispatch in ReferenceInspectionFixture.Dispatches
        select (browser, dispatch);

    /// <summary>
    /// Real source inspection rejects unchanged edits, accepts a correction, compares actual workers, and exports the corrected result.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="dispatch">The reference inspection or binding form.</param>
    [TestMethod]
    [DynamicData(nameof(ReferenceInspectionCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task LiveSession_EditRejectsReferenceInspection(string browser, string dispatch) =>
        AssertReferenceInspectionAsync(browser, dispatch, supported: false);

    /// <summary>
    /// Metadata-only BCL tokens and ordinary user lookalikes remain usable through comparison and save/reset/load in both engines.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="dispatch">The metadata token or user-defined lookalike.</param>
    [TestMethod]
    [DataRow("chromium", "token")]
    [DataRow("webkit", "token")]
    [DataRow("chromium", "lookalike")]
    [DataRow("webkit", "lookalike")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task LiveSession_ComparisonAllowsReferenceTokensAndLookalikes(string browser, string dispatch) =>
        AssertReferenceInspectionAsync(browser, dispatch, supported: true);

    private async Task AssertReferenceInspectionAsync(string browser, string dispatch, bool supported)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(ReferenceInspectionFixture.Create(dispatch));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/reference-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/reference-source.dll");
        await ExpectCompletionAsync(page, "types)");
        var runtime = dispatch == "runtime named delegate";
        var argument = runtime ? "ldstr \"GetReferencedAssemblies\"\n" : "";
        var signature = runtime ? "string" : "";
        await RunCorpusCellAsync(page, argument + "call int32 ReferenceInspection.Owner::Read(" + signature + ")\nret", 42);
        await TypeLineAsync(page, ".edit int32 ReferenceInspection.Owner::Read(" + signature + ") as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else
        {
            var problem = runtime ? "indirect reflection cannot prove a supported target" : ReferenceInspectionFixture.Problem;
            await ExpectComparisonTextAsync(page, runtime ? "supported" : "reproduce");
            var diagnostic = await BufferTextAsync(page);
            var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(problem, text);
            if (!runtime) Assert.Contains("GetReferencedAssemblies", text);
            await PromptContainsAsync(page, "}");
            await ClearPromptAsync(page);
            await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read("
                + (runtime ? "string name" : "") + ") {\nldc.i4.s 43\nret\n}\n}", "edit Copy committed as revision 1");
        }

        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, runtime ? ".compare Copy (\"GetReferencedAssemblies\")" : ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null,
                side.GetRawText());
            Assert.AreEqual(index == 0 || supported ? "42" : "43", side.GetProperty("result").GetProperty("value").GetString());
            Assert.AreEqual(1, side.GetProperty("invocations").GetArrayLength());
        }

        await ExpectComparisonTextAsync(page, supported ? "Copy: match" : "Copy: different");
        await RunCorpusCellAsync(page, argument + "call int32 ReferenceInspection.Owner::Read(" + signature + ")\nret", 42);
        if (runtime) await TypeLineAsync(page, "ldstr \"GetReferencedAssemblies\"");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/reference-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/reference-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/reference-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [reference-copy]IlRepl.Cell::Run()\nret", supported ? 42 : 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
