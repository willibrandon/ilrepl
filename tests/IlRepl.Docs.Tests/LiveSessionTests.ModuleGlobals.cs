using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser source module globals execute before lookup rejects recoverably; standalone tokens and user lookalikes remain usable.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] ModuleGlobalBrowsers = ["chromium", "webkit"];
    private static readonly (string Api, string Dispatch, bool Supported)[] ModuleGlobalSamples =
    [
        ("GetMethod(types)", "direct", false), ("GetMethod(binding)", "direct", false),
        ("GetMethods(flags)", "direct", false), ("GetField(flags)", "direct", false),
        ("GetFields", "direct", false), ("GetMethod", "invoke", false),
        ("GetFields", "invoke", false), ("GetMethods", "delegate", false),
        ("GetFields", "delegate", false), ("GetMethods", "pointer", false),
        ("GetMethod", "token", true), ("GetFields", "token", true),
        ("GetMethods", "lookalike", true), ("GetField", "lookalike", true),
    ];

    /// <summary>
    /// Exercises method and field tables, binding overloads, indirect calls and supported controls in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Api, string Dispatch, bool Supported)> ModuleGlobalCases =>
        from browser in ModuleGlobalBrowsers
        from sample in ModuleGlobalSamples
        select (browser, sample.Api, sample.Dispatch, sample.Supported);

    /// <summary>
    /// Real global methods and fields execute before rejected edits recover, compare in workers, and persist through exported reload.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="api">The global method or field lookup overload.</param>
    /// <param name="dispatch">The direct, indirect, standalone token or lookalike route.</param>
    /// <param name="supported">Whether the original operation retains supported copied behavior.</param>
    [TestMethod]
    [DynamicData(nameof(ModuleGlobalCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesModuleGlobals(string browser, string api,
        string dispatch, bool supported)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(ModuleGlobalFixture.Create(api, dispatch));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/global-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/global-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 ModuleGlobals.Owner::Read()\nret", 42);
        await TypeLineAsync(page, ".edit int32 ModuleGlobals.Owner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else
        {
            var problem = ModuleGlobalFixture.Problem;
            await ExpectComparisonTextAsync(page, "reproduce");
            var diagnostic = await BufferTextAsync(page);
            var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(problem, text);
            Assert.Contains(ModuleGlobalFixture.ApiName(api), text);
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
        await RunCorpusCellAsync(page, "call int32 ModuleGlobals.Owner::Read()\nret", 42);
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/global-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/global-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/global-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [global-copy]IlRepl.Cell::Run()\nret", supported ? 42 : 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
