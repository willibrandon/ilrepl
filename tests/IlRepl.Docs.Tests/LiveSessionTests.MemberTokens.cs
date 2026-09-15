using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser copied member tokens reject recoverably while external metadata, standalone tokens and user lookalikes remain usable.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] MemberTokenBrowsers = ["chromium", "webkit"];
    private static readonly (string Kind, string Dispatch, bool Supported)[] MemberTokenSamples =
    [
        ("Type", "direct", false), ("Method", "direct", false), ("Field", "direct", false),
        ("Property", "direct", false), ("Event", "direct", false), ("Parameter", "direct", false),
        ("Type", "invoke", false), ("Field", "property", false), ("Type", "named", false),
        ("Type", "invoke-member", false), ("Method", "unknown", false),
        ("Type", "external-named", true), ("Field", "external-property", true), ("Parameter", "external", true),
        ("Type", "external-sibling", true), ("Type", "token", true), ("Type", "lookalike", true),
        ("GenericParameter", "direct", true), ("Array", "direct", true), ("ByRef", "direct", false),
        ("Constructed", "direct", true), ("GenericTypeParameter", "direct", true),
    ];

    /// <summary>
    /// Exercises copied member families, reflected and delegate getters, external metadata and supported controls in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Kind, string Dispatch, bool Supported)> MemberTokenCases =>
        from browser in MemberTokenBrowsers
        from sample in MemberTokenSamples
        select (browser, sample.Kind, sample.Dispatch, sample.Supported);

    /// <summary>
    /// Real shifted member tokens execute before rejected edits recover, compare in workers, and persist through exported reload.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The reflected member family.</param>
    /// <param name="dispatch">The direct, indirect, standalone token or lookalike route.</param>
    /// <param name="supported">Whether the original operation retains supported copied behavior.</param>
    [TestMethod]
    [DynamicData(nameof(MemberTokenCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesMemberTokens(string browser, string kind,
        string dispatch, bool supported)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(MemberTokenFixture.Create(kind, dispatch));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/member-token-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.s 97\nret", 97);
        await TypeLineAsync(page, ".load /tmp/member-token-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 MemberTokens.Owner::Read()\nldc.i4.s 100\nadd\nret", 142);
        await TypeLineAsync(page, ".edit int32 MemberTokens.Owner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else
        {
            var problem = MemberTokenFixture.Problem;
            await ExpectComparisonTextAsync(page, "reproduce");
            var diagnostic = await BufferTextAsync(page);
            var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(problem, text);
            Assert.Contains("MetadataToken", text);
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
        await RunCorpusCellAsync(page, "call int32 MemberTokens.Owner::Read()\nldc.i4 200\nadd\nret", 242);
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/member-token-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/member-token-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/member-token-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [member-token-copy]IlRepl.Cell::Run()\nunbox.any int32\nldc.i4 300\nadd\nret",
            supported ? 342 : 343);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
