using System.Text.Json;
using IlRepl.Tests.Shared;
using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser type-name queries preserve unchanged names and reject changed copied names through workers and exported reload.
/// </summary>
public sealed partial class LiveSessionTests
{
    private static readonly string[] TypeNameBrowsers = ["chromium", "webkit"];

    /// <summary>
    /// Exercises every changed and supported type-name partition in both browser engines.
    /// </summary>
    public static IEnumerable<(string Browser, string Shape, string Api, string Dispatch)> TypeNameCases =>
        from browser in TypeNameBrowsers
        from sample in TypeNameFixture.Cases.Concat(TypeNameFixture.SupportedCases)
        select (browser, sample.Shape, sample.Api, sample.Dispatch);

    /// <summary>
    /// Real source names survive rejected drafts, safe edits, worker comparison and saved assembly reload without changing the source.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="shape">The copied, external, constructed, ordinary or null receiver.</param>
    /// <param name="api">The reflected type-name or ordinary member API.</param>
    /// <param name="dispatch">The direct, reflected, delegate, pointer or supported control route.</param>
    [TestMethod]
    [DynamicData(nameof(TypeNameCases))]
    [DoNotParallelize]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesTypeNames(string browser, string shape, string api, string dispatch)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var fixture = TypeNameFixture.Create(shape, api, dispatch);
        var supported = TypeNameFixture.SupportedCases.Contains((shape, api, dispatch));
        var original = "int32 [" + fixture.AssemblyName + "]TypeNames.Owner::Read()";
        await RunCorpusCellAsync(page, "ldstr \"/tmp/type-name-source.dll\"\nldstr \""
            + Convert.ToBase64String(fixture.Image) + "\"\ncall uint8[] Convert::FromBase64String(string)\n"
            + "call void File::WriteAllBytes(string, uint8[])\nldc.i4.s 97\nret", 97);
        await TypeLineAsync(page, ".load /tmp/type-name-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call " + original + "\nldc.i4.s 100\nadd\nret", 142);
        await TypeLineAsync(page, ".edit " + original + " as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        var parent = await ObserveComparisonResultsAsync(page);
        var nextResult = 0;
        if (supported)
        {
            await page.Keyboard.PressAsync("Enter");
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else await RejectTypeNameDraftAsync(page, api);

        var submitted = (await StoredHistoryAsync(page)).Last(entry => entry.StartsWith(".edit ", StringComparison.Ordinal));
        var initial = ".edit Copy " + submitted[submitted.IndexOf('{')..];
        var replacement = ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 43\nret\n}\n}";
        if (supported)
        {
            var position = initial.LastIndexOf("ret", StringComparison.Ordinal);
            Assert.IsGreaterThan(0, position);
            replacement = initial.Insert(position, "ldc.i4.1\nadd\n");
            await RunCorpusCellAsync(page, "call Copy\nldc.i4 200\nadd\nret", 242);
            await TypeLineAsync(page, ".compare Copy ()");
            AssertTypeNameSide(await WaitForComparisonResultAsync(parent, nextResult++), "42");
            AssertTypeNameSide(await WaitForComparisonResultAsync(parent, nextResult++), "42");
            await ExpectComparisonTextAsync(page, "Copy: match");
        }
        else
        {
            await SubmitEditSourceAsync(page, replacement, "edit Copy committed as revision 1");
            await PasteAsync(page, initial);
            await RejectTypeNameDraftAsync(page, api);
            await RunCorpusCellAsync(page, "call Copy\nldc.i4 200\nadd\nret", 243);
        }
        await SubmitEditSourceAsync(page, replacement, "edit Copy committed as revision 2");
        await RunCorpusCellAsync(page, "call Copy\nldc.i4 300\nadd\nret", 343);
        await RunCorpusCellAsync(page, "call " + original + "\nldc.i4 400\nadd\nret", 442);
        await TypeLineAsync(page, ".compare Copy ()");
        AssertTypeNameSide(await WaitForComparisonResultAsync(parent, nextResult++), "42");
        AssertTypeNameSide(await WaitForComparisonResultAsync(parent, nextResult), "43");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/type-name-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/type-name-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        if (supported)
        {
            await TypeLineAsync(page, ".load /tmp/type-name-source.dll");
            await ExpectCompletionAsync(page, "types)");
        }
        await TypeLineAsync(page, ".load /tmp/type-name-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [type-name-copy]IlRepl.Cell::Run()\nunbox.any int32\n"
            + "ldc.i4 500\nadd\nret", 543);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static async Task RejectTypeNameDraftAsync(IPage page, string api)
    {
        await page.Keyboard.PressAsync("Enter");
        await TypeNamePromptContainsAsync(page, "}");
        await TypeNameInputIdleAsync(page);
        var diagnostic = await TypeNameTranscriptAsync(page);
        Assert.Contains(TypeNameFixture.Problem, diagnostic);
        Assert.Contains(api, diagnostic);
        await ClearTypeNamePromptAsync(page);
    }

    private static Task<IJSHandle> TypeNamePromptContainsAsync(IPage page, string text) => page.WaitForFunctionAsync("""
        text => {
          const terminal = window.ilreplTerminal;
          const buffer = terminal.buffer.active;
          return buffer.getLine(buffer.baseY + terminal.rows - 2)?.translateToString(true).includes(text);
        }
        """, text, new() { PollingInterval = 16, Timeout = 120_000 });

    private static async Task ClearTypeNamePromptAsync(IPage page)
    {
        await TypeNameInputIdleAsync(page);
        await page.Keyboard.PressAsync("Control+c");
        await page.WaitForFunctionAsync("""
            () => {
              const terminal = window.ilreplTerminal;
              const buffer = terminal.buffer.active;
              const row = buffer.getLine(buffer.baseY + terminal.rows - 2)?.translateToString(true).trim() ?? '';
              const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
              return /^il\[\d+\]>$/.test(row) && !status.includes('updating') && !status.includes('sending')
                && !status.includes('cancelling') && !status.includes('Ctrl+C cancels');
            }
            """, null, new() { Timeout = 120_000 });
    }

    private static Task<IJSHandle> TypeNameInputIdleAsync(IPage page) => page.WaitForFunctionAsync("""
            () => {
              const terminal = window.ilreplTerminal;
              const buffer = terminal.buffer.active;
              const status = buffer.getLine(buffer.baseY + terminal.rows - 1)?.translateToString(true) ?? '';
              return !status.includes('updating') && !status.includes('sending')
                && !status.includes('cancelling') && !status.includes('Ctrl+C cancels');
            }
            """, null, new() { Timeout = 120_000 });

    private static async Task<string> TypeNameTranscriptAsync(IPage page)
    {
        var transcript = await page.EvaluateAsync<string>("""
            () => {
              const buffer = window.ilreplTerminal.buffer.active;
              return Array.from({ length: buffer.length }, (_, row) =>
                buffer.getLine(row)?.translateToString(true) ?? '').join('\n');
            }
            """);
        return string.Join(" ", transcript.Replace('│', ' ').Replace('▉', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AssertTypeNameSide(JsonElement side, string expected)
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
        var receiver = invocation.GetProperty("inputs").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "receiver").GetProperty("value");
        Assert.AreEqual("null", receiver.GetProperty("kind").GetString(), json);
        var returned = invocation.GetProperty("outputs").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "return").GetProperty("value");
        Assert.AreEqual("scalar", returned.GetProperty("kind").GetString(), json);
        Assert.AreEqual(expected, returned.GetProperty("value").GetString(), json);
    }
}
