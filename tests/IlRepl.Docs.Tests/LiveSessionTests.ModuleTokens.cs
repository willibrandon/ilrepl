using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser numeric module-token resolution rejects recoverably while tokens, user lookalikes and ordinary member lookup remain usable.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] ModuleTokenBrowsers = ["chromium", "webkit"];
    private static readonly (string Target, string Api, string Dispatch, bool Supported)[] ModuleTokenSamples =
    [
        ("Module", "ResolveMethod", "direct", false), ("Module", "ResolveField(context)", "direct", false),
        ("Module", "ResolveType", "direct", false), ("Module", "ResolveMember(context)", "direct", false),
        ("Module", "ResolveSignature", "direct", false), ("Module", "ResolveString", "invoke", false),
        ("Module", "ResolveString", "delegate", false), ("ModuleHandle", "ResolveMethodHandle(context)", "direct", false),
        ("ModuleHandle", "ResolveFieldHandle", "direct", false), ("ModuleHandle", "ResolveTypeHandle", "invoke", false),
        ("ModuleHandle", "GetRuntimeTypeHandleFromMetadataToken", "direct", false),
        ("Module", "ResolveString", "token", true), ("ModuleHandle", "ResolveTypeHandle", "token", true),
        ("Module", "ResolveMethod", "lookalike", true), ("Module", "ResolveField", "lookup", true),
    ];

    /// <summary>
    /// Exercises representative token kinds, generic-context overloads, indirect calls and supported controls in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Target, string Api, string Dispatch, bool Supported)> ModuleTokenCases =>
        from browser in ModuleTokenBrowsers
        from sample in ModuleTokenSamples
        select (browser, sample.Target, sample.Api, sample.Dispatch, sample.Supported);

    /// <summary>
    /// Real source token resolution executes before rejected edits recover, compare in actual workers, and persist through exported reload.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="target">The Module or ModuleHandle receiver.</param>
    /// <param name="api">The numeric token resolver.</param>
    /// <param name="dispatch">The direct, indirect, standalone token or lookalike route.</param>
    /// <param name="supported">Whether the original operation retains supported copied behavior.</param>
    [TestMethod]
    [DynamicData(nameof(ModuleTokenCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesModuleTokenPolicy(string browser, string target, string api,
        string dispatch, bool supported)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(ModuleTokenFixture.Create(target, api, dispatch));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/token-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/token-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 ModuleTokens.Owner::Read()\nret", 42);
        await TypeLineAsync(page, ".edit int32 ModuleTokens.Owner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else
        {
            var problem = ModuleTokenFixture.Problem;
            await ExpectComparisonTextAsync(page, "reproduce");
            var diagnostic = await BufferTextAsync(page);
            var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(problem, text);
            Assert.Contains(ModuleTokenFixture.ApiName(api), text);
            await PromptContainsAsync(page, "}");
            await ClearPromptAsync(page);
            await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 43\nret\n}\n}",
                "edit Copy committed as revision 1");
        }

        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
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
        await RunCorpusCellAsync(page, "call int32 ModuleTokens.Owner::Read()\nret", 42);
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/token-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/token-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/token-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [token-copy]IlRepl.Cell::Run()\nret", supported ? 42 : 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
