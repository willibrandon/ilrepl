using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons distinguish copied source helper signatures from genuine external nominal boundaries.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Source helpers preserve exact copied signatures while retained fields and independent types keep their original identities.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The exact parameter, return, or field signature shape.</param>
    [TestMethod]
    [DataRow("chromium", "parameter-modifier")]
    [DataRow("webkit", "parameter-modifier")]
    [DataRow("chromium", "parameter-pointer-modifier")]
    [DataRow("webkit", "parameter-pointer-modifier")]
    [DataRow("chromium", "parameter-function-return")]
    [DataRow("webkit", "parameter-function-return")]
    [DataRow("chromium", "parameter-function-parameter")]
    [DataRow("webkit", "parameter-function-parameter")]
    [DataRow("chromium", "parameter-function-modifier")]
    [DataRow("webkit", "parameter-function-modifier")]
    [DataRow("chromium", "return-modifier")]
    [DataRow("webkit", "return-modifier")]
    [DataRow("chromium", "return-function")]
    [DataRow("webkit", "return-function")]
    [DataRow("chromium", "field-modifier")]
    [DataRow("webkit", "field-modifier")]
    [DataRow("chromium", "field-function")]
    [DataRow("webkit", "field-function")]
    [DataRow("chromium", "field-array-modifier")]
    [DataRow("webkit", "field-array-modifier")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonCopiesExactSourceHelperSignatures(string browser, string kind)
    {
        foreach (var copiedType in new[] { true, false })
            await CheckExactBoundaryAsync(browser, kind, copiedType, separate: false);
    }

    /// <summary>
    /// Two real assemblies retain exact boundary diagnostics, compatible external signatures, corrected comparisons and exports.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The exact parameter, return, or field signature shape.</param>
    [TestMethod]
    [DataRow("chromium", "parameter-modifier")]
    [DataRow("webkit", "parameter-modifier")]
    [DataRow("chromium", "parameter-pointer-modifier")]
    [DataRow("webkit", "parameter-pointer-modifier")]
    [DataRow("chromium", "parameter-function-return")]
    [DataRow("webkit", "parameter-function-return")]
    [DataRow("chromium", "parameter-function-parameter")]
    [DataRow("webkit", "parameter-function-parameter")]
    [DataRow("chromium", "parameter-function-modifier")]
    [DataRow("webkit", "parameter-function-modifier")]
    [DataRow("chromium", "return-modifier")]
    [DataRow("webkit", "return-modifier")]
    [DataRow("chromium", "return-function")]
    [DataRow("webkit", "return-function")]
    [DataRow("chromium", "field-modifier")]
    [DataRow("webkit", "field-modifier")]
    [DataRow("chromium", "field-function")]
    [DataRow("webkit", "field-function")]
    [DataRow("chromium", "field-array-modifier")]
    [DataRow("webkit", "field-array-modifier")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonValidatesExactExternalBoundaries(string browser, string kind)
    {
        foreach (var copiedType in new[] { true, false })
            await CheckExactBoundaryAsync(browser, kind, copiedType, separate: true);
    }

    private async Task CheckExactBoundaryAsync(string browser, string kind, bool copiedType, bool separate)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var images = separate ? ExactBoundaryFixture.CreateExternal(kind, copiedType)
            : (Source: ExactBoundaryFixture.Create(kind, copiedType), Helper: Array.Empty<byte>());
        using var sourceReader = new PEReader(new MemoryStream(images.Source));
        var metadata = sourceReader.GetMetadataReader();
        var sourceName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        var sourceReference = "int32 [" + sourceName + "]Owner::Read()";
        var write = "ldstr \"/tmp/boundary-source.dll\"\nldstr \"" + Convert.ToBase64String(images.Source) + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\n";
        if (separate)
        {
            write += "ldstr \"/tmp/boundary-helper.dll\"\nldstr \"" + Convert.ToBase64String(images.Helper) + "\"\n"
                + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\n";
        }
        await RunCorpusCellAsync(page, write + "ldc.i4.s 97\nret", 97);
        await TypeLineAsync(page, ".load /tmp/boundary-source.dll");
        await ExpectCompletionAsync(page, "types)");
        if (separate)
        {
            await TypeLineAsync(page, ".load /tmp/boundary-helper.dll");
            await ExpectCompletionAsync(page, "types)");
        }
        await RunCorpusCellAsync(page, "call " + sourceReference + "\nldc.i4.s 100\nadd\nret", 142);
        await TypeLineAsync(page, ".edit " + sourceReference + " as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        var blocked = copiedType && (separate || kind.StartsWith("field", StringComparison.Ordinal));
        var source = ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 43\nret\n}\n}";
        var parent = await ObserveComparisonResultsAsync(page);
        var nextResult = 0;
        if (blocked)
        {
            await ClearPromptAsync(page);
            await page.Keyboard.TypeAsync(".methods Copy");
            await PromptContainsAsync(page, ".methods Copy");
            await page.Keyboard.PressAsync("Enter");
            await EmptyPromptAsync(page);
            await InputIdleAsync(page);
            var transcript = (await page.EvaluateAsync<string>("""
                () => {
                  const buffer = window.ilreplTerminal.buffer.active;
                  return Array.from({ length: buffer.length }, (_, row) =>
                    buffer.getLine(row)?.translateToString(true) ?? '').join('\n');
                }
                """)).Replace('│', ' ').Replace('▉', ' ');
            Assert.Contains("original nominal type", string.Join(' ', transcript.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)));
        }
        else
        {
            await page.Keyboard.PressAsync("Enter");
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
            var history = await StoredHistoryAsync(page);
            var submitted = history.Last(entry => entry.StartsWith(".edit ", StringComparison.Ordinal));
            source = ".edit Copy " + submitted[submitted.IndexOf('{')..];
            var returnPosition = source.LastIndexOf("ret", StringComparison.Ordinal);
            Assert.IsGreaterThan(0, returnPosition);
            source = source.Insert(returnPosition, "ldc.i4.1\nadd\n");
            await RunCorpusCellAsync(page, "call Copy\nldc.i4 200\nadd\nret", 242);
            await TypeLineAsync(page, ".compare Copy ()");
            AssertExactBoundarySide(await WaitForComparisonResultAsync(parent, nextResult++), "42");
            AssertExactBoundarySide(await WaitForComparisonResultAsync(parent, nextResult++), "42");
            await ExpectComparisonTextAsync(page, "Copy: match");
        }
        await SubmitEditSourceAsync(page, source, "edit Copy committed as revision " + (blocked ? "1" : "2"));
        await RunCorpusCellAsync(page, "call Copy\nldc.i4 300\nadd\nret", 343);
        await RunCorpusCellAsync(page, "call " + sourceReference + "\nldc.i4 400\nadd\nret", 442);
        await TypeLineAsync(page, ".compare Copy ()");
        AssertExactBoundarySide(await WaitForComparisonResultAsync(parent, nextResult++), "42");
        AssertExactBoundarySide(await WaitForComparisonResultAsync(parent, nextResult), "43");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/exact-boundary-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/exact-boundary-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        if (!copiedType)
        {
            await TypeLineAsync(page, ".load /tmp/boundary-source.dll");
            await ExpectCompletionAsync(page, "types)");
            if (separate)
            {
                await TypeLineAsync(page, ".load /tmp/boundary-helper.dll");
                await ExpectCompletionAsync(page, "types)");
            }
        }
        await TypeLineAsync(page, ".load /tmp/exact-boundary-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [exact-boundary-copy]IlRepl.Cell::Run()\nunbox.any int32\n"
            + "ldc.i4 500\nadd\nret", 543);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertExactBoundarySide(JsonElement side, string expected)
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
