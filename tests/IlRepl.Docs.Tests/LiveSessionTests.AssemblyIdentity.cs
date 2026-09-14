using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser assembly identity inspection rejects changed owner identity while ordinary metadata and lookalike calls remain supported.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] IdentityBrowsers = ["chromium", "webkit"];
    private static readonly (string Api, string Dispatch, string Receiver)[] IdentitySamples =
    [
        ("GetName", "direct", "executing"),
        ("GetName(bool)", "direct", "executing"),
        ("FullName", "property", "executing"),
        ("ToString", "direct", "executing"),
        ("ToString", "object invoke", "executing"),
        ("ToString", "open method delegate", "executing"),
        ("GetName", "named delegate", "owner"),
        ("FullName", "direct", "module"),
        ("ToString", "runtime named delegate", "executing"),
    ];

    /// <summary>
    /// Exercises identity API variants, indirect calls, and assembly receiver flows in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Api, string Dispatch, string Receiver)> AssemblyIdentityCases =>
        from browser in IdentityBrowsers
        from sample in IdentitySamples
        select (browser, sample.Api, sample.Dispatch, sample.Receiver);

    /// <summary>
    /// Real source inspection rejects unchanged edits, accepts a correction, compares actual workers, and exports the corrected result.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="api">The assembly identity API or property.</param>
    /// <param name="dispatch">The direct, reflection, or delegate binding form.</param>
    /// <param name="receiver">The executing assembly, owner type, or module receiver flow.</param>
    [TestMethod]
    [DynamicData(nameof(AssemblyIdentityCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task LiveSession_EditRejectsAssemblyIdentity(string browser, string api, string dispatch, string receiver) =>
        AssertAssemblyIdentityAsync(browser, api, dispatch, receiver, supported: false);

    /// <summary>
    /// Metadata tokens, ordinary object calls, and user, Type, and member metadata remain usable through comparison and exported reload.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="api">The ordinary metadata API.</param>
    /// <param name="dispatch">The supported metadata token or user/type/member operation.</param>
    [TestMethod]
    [DataRow("chromium", "FullName", "token")]
    [DataRow("webkit", "FullName", "token")]
    [DataRow("chromium", "GetName", "lookalike")]
    [DataRow("webkit", "GetName", "lookalike")]
    [DataRow("chromium", "FullName", "type metadata")]
    [DataRow("webkit", "FullName", "type metadata")]
    [DataRow("chromium", "Name", "member metadata")]
    [DataRow("webkit", "Name", "member metadata")]
    [DataRow("chromium", "ToString", "ordinary tostring")]
    [DataRow("webkit", "ToString", "ordinary tostring")]
    [DataRow("chromium", "ToString", "ordinary delegate")]
    [DataRow("webkit", "ToString", "ordinary delegate")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public Task LiveSession_ComparisonAllowsIdentityMetadataAndLookalikes(string browser, string api, string dispatch) =>
        AssertAssemblyIdentityAsync(browser, api, dispatch, "executing", supported: true);

    private async Task AssertAssemblyIdentityAsync(string browser, string api, string dispatch, string receiver, bool supported)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(AssemblyIdentityFixture.Create(api, dispatch, receiver));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/identity-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/identity-source.dll");
        await ExpectCompletionAsync(page, "types)");
        var runtime = dispatch == "runtime named delegate";
        var unproven = runtime || dispatch == "open method delegate";
        var argument = runtime ? "ldstr \"ToString\"\n" : "";
        var signature = runtime ? "string" : "";
        await RunCorpusCellAsync(page, argument + "call int32 AssemblyIdentity.Owner::Read(" + signature + ")\nret", 42);
        await TypeLineAsync(page, ".edit int32 AssemblyIdentity.Owner::Read(" + signature + ") as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else
        {
            var problem = unproven ? "indirect reflection cannot prove a supported target" : AssemblyIdentityFixture.Problem;
            await ExpectComparisonTextAsync(page, unproven ? "supported" : "reproduce");
            var diagnostic = await BufferTextAsync(page);
            var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(problem, text);
            if (!unproven)
                Assert.Contains(api switch { "FullName" => "get_FullName", "GetName(bool)" => "GetName", _ => api }, text);
            await PromptContainsAsync(page, "}");
            await ClearPromptAsync(page);
            await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read("
                + (runtime ? "string name" : "") + ") {\nldc.i4.s 43\nret\n}\n}", "edit Copy committed as revision 1");
        }

        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, runtime ? ".compare Copy (\"ToString\")" : ".compare Copy ()");
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
        await RunCorpusCellAsync(page, argument + "call int32 AssemblyIdentity.Owner::Read(" + signature + ")\nret", 42);
        if (runtime) await TypeLineAsync(page, "ldstr \"ToString\"");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/identity-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/identity-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/identity-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [identity-copy]IlRepl.Cell::Run()\nret", supported ? 42 : 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
