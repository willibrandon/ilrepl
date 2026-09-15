using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser metadata reference observations retain recoverable edits while ordinary identity, null checks and tokens remain executable.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] MetadataReferenceBrowsers = ["chromium", "webkit"];
    private static readonly (string Target, string Operation, string Flow, bool Supported)[] MetadataReferenceSamples =
    [
        ("Assembly", "equality", "direct", false), ("Module", "inequality", "direct", false),
        ("Assembly", "hash", "direct", false), ("Module", "reference-equals", "direct", false),
        ("Module", "object-equals", "helper-arguments", false), ("Assembly", "ceq", "helper-return", false),
        ("Module", "ceq", "local", false), ("Module", "beq", "direct", false),
        ("Assembly", "virtual-equals", "invoke", false), ("Module", "virtual-equals", "delegate", false),
        ("Assembly", "equals", "named-delegate", false), ("Assembly", "reference-equals", "invoke", false),
        ("Object", "reference-equals", "distinct", true), ("Object", "virtual-equals", "delegate", true),
        ("String", "equality", "direct", true), ("Assembly", "ceq", "null", true),
        ("Module", "reference-equals", "null", true), ("Assembly", "equality", "token", true),
        ("ModuleHandle", "equality", "direct", false), ("Module", "runtime-hash", "direct", false),
        ("Assembly", "reference-equals", "field", false),
    ];

    /// <summary>
    /// Exercises representative direct, indirect, provenance, raw IL and supported cases in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Target, string Operation, string Flow, bool Supported)> AssemblyReferenceCases =>
        from browser in MetadataReferenceBrowsers
        from sample in MetadataReferenceSamples
        select (browser, sample.Target, sample.Operation, sample.Flow, sample.Supported);

    /// <summary>
    /// Real source identities execute before rejected edits recover, compare in actual workers, and persist through exported reload.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="target">The metadata or ordinary value family.</param>
    /// <param name="operation">The identity observation or metadata token operation.</param>
    /// <param name="flow">The provenance, invocation, or supported control route.</param>
    /// <param name="supported">Whether the original operation retains supported copied behavior.</param>
    [TestMethod]
    [DynamicData(nameof(AssemblyReferenceCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesMetadataReferencePolicy(string browser, string target, string operation,
        string flow, bool supported)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(AssemblyReferenceFixture.Create(target, operation, flow));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/reference-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/reference-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 AssemblyReferences.Owner::Read()\nret", 42);
        await TypeLineAsync(page, ".edit int32 AssemblyReferences.Owner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else
        {
            var problem = AssemblyReferenceFixture.Reason(operation, flow);
            await ExpectComparisonTextAsync(page, problem == AssemblyReferenceFixture.Problem ? "identity" : "supported");
            var diagnostic = await BufferTextAsync(page);
            var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(problem, text);
            if (problem == AssemblyReferenceFixture.Problem && AssemblyReferenceFixture.Api(operation) is { } api)
                Assert.Contains(api, text);
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
        await RunCorpusCellAsync(page, "call int32 AssemblyReferences.Owner::Read()\nret", 42);
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
