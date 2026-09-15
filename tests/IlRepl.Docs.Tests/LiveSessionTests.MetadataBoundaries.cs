using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Real browser inspectors preserve unchanged metadata and reject copied identities passed into a different assembly.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] MetadataBoundaryBrowsers = ["chromium", "webkit"];

    /// <summary>
    /// Enumerates each copied and supported metadata boundary partition in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Target, string Flow, string Origin)> MetadataBoundaryCases =>
        from browser in MetadataBoundaryBrowsers
        from sample in MetadataBoundaryFixture.RejectedCases.Concat(MetadataBoundaryFixture.SupportedCases)
        select (browser, sample.Target, sample.Flow, sample.Origin);

    /// <summary>
    /// Actual source identity survives unsupported edit recovery and supported copies across workers and exported reload.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="target">The metadata or ordinary argument family.</param>
    /// <param name="flow">The typed, erased, stored, helper, boxed, or whole-array route.</param>
    /// <param name="origin">The copied owner, unchanged sibling, BCL, ordinary object, or null producer.</param>
    [TestMethod]
    [DynamicData(nameof(MetadataBoundaryCases))]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesExternalMetadataBoundary(string browser, string target, string flow, string origin)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var images = MetadataBoundaryFixture.Create(target, flow, origin);
        var write = "ldstr \"/tmp/metadata-boundary-source.dll\"\nldstr \"" + Convert.ToBase64String(images.Source) + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\n"
            + "ldstr \"/tmp/metadata-boundary-inspector.dll\"\nldstr \"" + Convert.ToBase64String(images.Inspector) + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.s 97\nret";
        await RunCorpusCellAsync(page, write, 97);
        await TypeLineAsync(page, ".load /tmp/metadata-boundary-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await TypeLineAsync(page, ".load /tmp/metadata-boundary-inspector.dll");
        await ExpectCompletionAsync(page, "types)");
        var original = "int32 [" + images.SourceName + "]MetadataBoundary.Owner::Read()";
        await RunCorpusCellAsync(page, "call " + original + "\nldc.i4.s 100\nadd\nret", 142);
        await TypeLineAsync(page, ".edit " + original + " as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        var supported = MetadataBoundaryFixture.IsSupported(flow, origin);
        var parent = await ObserveComparisonResultsAsync(page);
        var nextResult = 0;
        var replacement = ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 43\nret\n}\n}";
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
            var submitted = (await StoredHistoryAsync(page)).Last(entry => entry.StartsWith(".edit ", StringComparison.Ordinal));
            var source = ".edit Copy " + submitted[submitted.IndexOf('{')..];
            var position = source.LastIndexOf("ret", StringComparison.Ordinal);
            Assert.IsGreaterThan(0, position);
            replacement = source.Insert(position, "ldc.i4.1\nadd\n");
            await RunCorpusCellAsync(page, "call Copy\nldc.i4 200\nadd\nret", 242);
            await TypeLineAsync(page, ".compare Copy ()");
            AssertMetadataBoundarySide(await WaitForComparisonResultAsync(parent, nextResult++), "42");
            AssertMetadataBoundarySide(await WaitForComparisonResultAsync(parent, nextResult++), "42");
            await ExpectComparisonTextAsync(page, "Copy: match");
        }
        else
        {
            await ExpectComparisonTextAsync(page, "identity");
            await PromptContainsAsync(page, "}");
            await ClearPromptAsync(page);
            await page.Keyboard.TypeAsync(".methods Copy");
            await PromptContainsAsync(page, ".methods Copy");
            await page.Keyboard.PressAsync("Enter");
            await EmptyPromptAsync(page);
            await InputIdleAsync(page);
            var transcript = await page.EvaluateAsync<string>("""
                () => {
                  const buffer = window.ilreplTerminal.buffer.active;
                  return Array.from({ length: buffer.length }, (_, row) =>
                    buffer.getLine(row)?.translateToString(true) ?? '').join('\n');
                }
                """);
            var diagnostic = string.Join(" ", transcript.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains("external", diagnostic);
            Assert.Contains("copied metadata identity", diagnostic);
            Assert.Contains(MetadataBoundaryFixture.Sink(flow), diagnostic);
        }
        await SubmitEditSourceAsync(page, replacement, "edit Copy committed as revision " + (supported ? "2" : "1"));
        await RunCorpusCellAsync(page, "call Copy\nldc.i4 300\nadd\nret", 343);
        if (flow.StartsWith("callback-", StringComparison.Ordinal))
            await RunCorpusCellAsync(page, "ldsfld int32 [" + images.SourceName
                + "]MetadataBoundary.Owner::Calls\nldc.i4 600\nadd\nret", 601);
        await RunCorpusCellAsync(page, "call " + original + "\nldc.i4 400\nadd\nret", 442);
        await TypeLineAsync(page, ".compare Copy ()");
        AssertMetadataBoundarySide(await WaitForComparisonResultAsync(parent, nextResult++), "42");
        AssertMetadataBoundarySide(await WaitForComparisonResultAsync(parent, nextResult), "43");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/metadata-boundary-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/metadata-boundary-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        if (supported)
        {
            await TypeLineAsync(page, ".load /tmp/metadata-boundary-source.dll");
            await ExpectCompletionAsync(page, "types)");
            await TypeLineAsync(page, ".load /tmp/metadata-boundary-inspector.dll");
            await ExpectCompletionAsync(page, "types)");
        }
        await TypeLineAsync(page, ".load /tmp/metadata-boundary-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [metadata-boundary-copy]IlRepl.Cell::Run()\nunbox.any int32\n"
            + "ldc.i4 500\nadd\nret", 543);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertMetadataBoundarySide(JsonElement side, string expected)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var result), json);
        Assert.AreEqual(JsonValueKind.Object, result.ValueKind, json);
        Assert.AreEqual("scalar", result.GetProperty("kind").GetString(), json);
        Assert.AreEqual(expected, result.GetProperty("value").GetString(), json);
        var invocation = Assert.ContainsSingle(side.GetProperty("invocations").EnumerateArray());
        Assert.IsFalse(invocation.TryGetProperty("exception", out exception) && exception.ValueKind != JsonValueKind.Null, json);
        var returned = invocation.GetProperty("outputs").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "return").GetProperty("value");
        Assert.AreEqual("scalar", returned.GetProperty("kind").GetString(), json);
        Assert.AreEqual(expected, returned.GetProperty("value").GetString(), json);
    }
}
