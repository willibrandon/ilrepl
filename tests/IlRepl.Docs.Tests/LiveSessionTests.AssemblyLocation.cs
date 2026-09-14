using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser source metadata policy reflects the actual stream loader and preserves supported runtime constants and ordinary metadata.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] LocationBrowsers = ["chromium", "webkit"];
    private static readonly (string Target, string Api, string Dispatch, bool Supported)[] LocationSamples =
    [
        ("Assembly", "Location", "direct", false), ("Assembly", "Location", "property", false),
        ("Assembly", "Location", "delegate", false), ("Assembly", "ImageRuntimeVersion", "direct", false),
        ("Module", "ScopeName", "property", false), ("Module", "ModuleVersionId", "direct", false),
        ("Module", "ToString", "object", false), ("Assembly", "ReflectionOnly", "direct", true),
        ("Assembly", "GlobalAssemblyCache", "direct", true), ("Assembly", "HostContext", "direct", true),
        ("Assembly", "IsFullyTrusted", "direct", true), ("Assembly", "Location", "token", true),
        ("Module", "ModuleVersionId", "token", true), ("Assembly", "Location", "lookalike", true),
        ("Type", "Name", "direct", true),
        ("ModuleHandle", "MDStreamVersion", "direct", false),
    ];

    /// <summary>
    /// Covers actual stream-loaded browser originals and stable metadata controls in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Target, string Api, string Dispatch, bool Supported)> AssemblyLocationCases =>
        from browser in LocationBrowsers
        from sample in LocationSamples
        select (browser, sample.Target, sample.Api, sample.Dispatch, sample.Supported);

    /// <summary>
    /// Real virtual-file loading proves empty source Location while image metadata, correction, workers, and exports remain observable.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="target">The Assembly, Module, ModuleHandle, or ordinary Type receiver.</param>
    /// <param name="api">The actual source metadata API.</param>
    /// <param name="dispatch">The direct, reflected, delegate, or metadata-token operation.</param>
    /// <param name="supported">Whether this operation retains supported behavior.</param>
    [TestMethod]
    [DynamicData(nameof(AssemblyLocationCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task LiveSession_ComparisonPreservesSourceMetadataPolicy(string browser, string target, string api, string dispatch,
        bool supported) => AssertAssemblyLocationAsync(browser, target, api, dispatch, supported);

    /// <summary>
    /// The browser's actual noncollectible session original retains its context after rejecting and correcting a copied observation.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesSourceCollectibility(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, AssemblyLocationFixture.CollectibleSource(collectible: false), "end of method Read");
        await RunCorpusCellAsync(page, "call Read\nret", 42);
        await TypeLineAsync(page, ".edit Read as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        await ExpectComparisonTextAsync(page, "reproduce");
        var diagnostic = await BufferTextAsync(page);
        var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(AssemblyLocationFixture.Problem("Assembly", "IsCollectible"), text);
        Assert.Contains("IsCollectible", text);
        await PromptContainsAsync(page, "}");
        await ClearPromptAsync(page);
        await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 43\nret\n}\n}",
            "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null,
                side.GetRawText());
            Assert.AreEqual(index == 0 ? "42" : "43", side.GetProperty("result").GetProperty("value").GetString());
            Assert.AreEqual(1, side.GetProperty("invocations").GetArrayLength());
        }
        await ExpectComparisonTextAsync(page, "Copy: different");
        await RunCorpusCellAsync(page, "call Read\nret", 42);
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/collectible-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/collectible-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/collectible-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [collectible-copy]IlRepl.Cell::Run()\nret", 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private async Task AssertAssemblyLocationAsync(string browser, string target, string api, string dispatch, bool supported)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var fixture = AssemblyLocationFixture.Create(target, api, dispatch, "/tmp/source-image.dll", stream: true);
        var image = Convert.ToBase64String(fixture.Image);
        await RunCorpusCellAsync(page, "ldstr \"/tmp/source-image.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/source-image.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "ldtoken SourceInspection.Owner\n"
            + "call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n"
            + "callvirt instance class Assembly Type::get_Assembly()\ncallvirt instance string Assembly::get_Location()\n"
            + "callvirt instance int32 String::get_Length()\nret", 0);
        var argument = "ldstr " + JsonSerializer.Serialize(fixture.Expected) + "\n";
        const string signature = "string";
        await RunCorpusCellAsync(page, argument + "call int32 SourceInspection.Owner::Read(string)\nret", 42);
        await TypeLineAsync(page, ".edit int32 SourceInspection.Owner::Read(" + signature + ") as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else
        {
            var problem = AssemblyLocationFixture.Problem(target, api);
            await ExpectComparisonTextAsync(page, "reproduce");
            var diagnostic = await BufferTextAsync(page);
            var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(problem, text);
            Assert.Contains(api, text);
            await PromptContainsAsync(page, "}");
            await ClearPromptAsync(page);
            await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read("
                + "string expected) {\nldc.i4.s 43\nret\n}\n}", "edit Copy committed as revision 1");
        }

        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy (" + JsonSerializer.Serialize(fixture.Expected) + ")");
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
        await RunCorpusCellAsync(page, argument + "call int32 SourceInspection.Owner::Read(" + signature + ")\nret", 42);
        await TypeLineAsync(page, "ldstr " + JsonSerializer.Serialize(fixture.Expected));
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/source-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/source-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/source-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [source-copy]IlRepl.Cell::Run()\nret", supported ? 42 : 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
