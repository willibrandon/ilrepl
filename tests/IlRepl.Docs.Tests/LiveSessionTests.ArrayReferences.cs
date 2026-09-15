using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser array-carried metadata references reject recoverably while ordinary elements, typed arrays and null slots remain executable.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] ArrayReferenceBrowsers = ["chromium", "webkit"];
    private static readonly (string Target, string Operation, string Flow, bool Supported)[] ArrayReferenceSamples =
    [
        ("Assembly", "reference-equals", "array", false), ("Module", "object-equals", "array", false),
        ("Assembly", "virtual-hash", "array", false), ("Module", "ceq", "alias", false),
        ("Assembly", "reference-equals", "field", false), ("Module", "ceq", "helper-read", false),
        ("Assembly", "reference-equals", "helper-write", false), ("Module", "object-equals", "helper-return", false),
        ("Assembly", "reference-equals", "unknown-index", false), ("Module", "reference-equals", "address", false),
        ("Assembly", "ceq", "merged", false), ("Assembly", "virtual-equals", "invoke", false),
        ("Assembly", "reference-equals", "get-value", false), ("Module", "ceq", "get-value-indices", false),
        ("String", "object-equals", "get-value", true), ("Object", "ceq", "safe-slot", true),
        ("Assembly", "reference-equals", "null-slot", true),
        ("Object", "reference-equals", "helper-write", true), ("String", "object-equals", "helper-return", true),
    ];

    /// <summary>
    /// Exercises representative direct, indirect, provenance, raw IL and supported cases in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Target, string Operation, string Flow, bool Supported)> ArrayReferenceCases =>
        from browser in ArrayReferenceBrowsers
        from sample in ArrayReferenceSamples
        select (browser, sample.Target, sample.Operation, sample.Flow, sample.Supported);

    /// <summary>
    /// Real source identities execute before rejected edits recover, compare in actual workers, and persist through exported reload.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="target">The metadata or ordinary value family.</param>
    /// <param name="operation">The identity observation performed on array elements.</param>
    /// <param name="flow">The provenance, invocation, or supported control route.</param>
    /// <param name="supported">Whether the original operation retains supported copied behavior.</param>
    [TestMethod]
    [DynamicData(nameof(ArrayReferenceCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesArrayReferencePolicy(string browser, string target, string operation,
        string flow, bool supported)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(ArrayReferenceFixture.Create(target, operation, flow));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/array-reference-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/array-reference-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 ArrayReferences.Owner::Read()\nldc.i4.s 100\nadd\nret", 142);
        await TypeLineAsync(page, ".edit int32 ArrayReferences.Owner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else
        {
            var problem = ArrayReferenceFixture.Reason(operation, flow);
            await ExpectComparisonTextAsync(page, problem == ArrayReferenceFixture.Problem ? "identity" : "supported");
            var diagnostic = await BufferTextAsync(page);
            var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(problem, text);
            if (problem == ArrayReferenceFixture.Problem && ArrayReferenceFixture.Api(operation) is { } api)
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
        await RunCorpusCellAsync(page, "call int32 ArrayReferences.Owner::Read()\nldc.i4 300\nadd\nret", 342);
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/array-reference-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/array-reference-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/array-reference-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [array-reference-copy]IlRepl.Cell::Run()\nunbox.any int32\nldc.i4 400\nadd\nret",
            supported ? 442 : 443);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
