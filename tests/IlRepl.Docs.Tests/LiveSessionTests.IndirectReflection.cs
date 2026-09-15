using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Indirect reflection receives the same recoverable preflight in both actual browser engines.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] IndirectBrowsers = ["chromium", "webkit"];
    private static readonly (string Target, string Api, string Dispatch)[] IndirectSamples =
    [
        ("Assembly", "GetTypes", "invoke"),
        ("Assembly", "GetTypes", "invoke options"),
        ("Assembly", "GetTypes", "handle"),
        ("Assembly", "GetTypes", "unknown"),
        ("Assembly", "GetTypes", "custom resolver"),
        ("Assembly", "GetType", "invoke"),
        ("Module", "GetTypes", "ireflect"),
        ("Assembly", "GetTypes", "invoke member"),
        ("Assembly", "DefinedTypes", "property"),
        ("Module", "CustomAttributes", "property options"),
        ("Assembly", "GetManifestResourceNames", "delegate"),
        ("Assembly", "GetCustomAttributesData", "method delegate"),
        ("Assembly", "GetTypes", "invoker"),
        ("Assembly", "GetTypes", "named delegate"),
        ("Assembly", "GetTypes", "helper"),
    ];

    /// <summary>
    /// Covers every indirect invocation family and metadata category in Chromium and WebKit.
    /// </summary>
    public static IEnumerable<(string Browser, string Target, string Api, string Dispatch)> IndirectReflectionCases =>
        from browser in IndirectBrowsers
        from sample in IndirectSamples
        select (browser, sample.Target, sample.Api, sample.Dispatch);

    /// <summary>
    /// Actual original execution, rejected edits, correction, worker comparison, and save/reset/load stay consistent in the browser.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="target">The metadata receiver.</param>
    /// <param name="api">The indirectly reached unsupported API.</param>
    /// <param name="dispatch">The invocation or binding operation.</param>
    [TestMethod]
    [DynamicData(nameof(IndirectReflectionCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditRejectsIndirectReflection(string browser, string target, string api, string dispatch)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(IndirectReflectionFixture.Create(target, api, dispatch));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/indirect.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/indirect.dll");
        await ExpectCompletionAsync(page, "types)");
        var unknown = dispatch == "unknown";
        var argument = unknown ? "ldstr \"GetTypes\"\n" : "";
        var signature = unknown ? "string" : "";
        await RunCorpusCellAsync(page, argument + "call int32 IndirectReflection.Owner::Read(" + signature + ")\nret", 42);
        await TypeLineAsync(page, ".edit int32 IndirectReflection.Owner::Read(" + signature + ") as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        var unproven = unknown || dispatch == "custom resolver";
        var problem = unproven ? "indirect reflection cannot prove a supported target" : IndirectReflectionFixture.Problem(api);
        var completion = unproven ? "supported target" : api == "GetType" ? "translate copied names"
            : problem.Contains("resources", StringComparison.Ordinal) ? "assembly's resources"
            : problem.Contains("attribute", StringComparison.Ordinal) ? "original metadata" : "complete type set";
        await ExpectComparisonTextAsync(page, completion);
        var diagnostic = await BufferTextAsync(page);
        var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(problem, text);
        await PromptContainsAsync(page, "}");
        await ClearPromptAsync(page);
        await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read("
            + (unknown ? "string name" : "") + ") {\nldc.i4.s 42\nret\n}\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, unknown ? ".compare Copy (\"GetTypes\")" : ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("42", side.GetProperty("result").GetProperty("value").GetString());
            Assert.AreEqual(1, side.GetProperty("invocations").GetArrayLength());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        if (unknown) await TypeLineAsync(page, "ldstr \"GetTypes\"");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/indirect-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/indirect-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/indirect-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [indirect-copy]IlRepl.Cell::Run()\nret", 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
