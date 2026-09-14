using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits diagnose copied nominal types inside exact external signatures and retain compatible references.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Preflight explains exact signature conflicts before revisions can remove them and compare with the original.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The external signature shape.</param>
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
    public async Task LiveSession_EditValidatesExactExternalBoundaries(string browser, string kind)
    {
        foreach (var copiedType in new[] { true, false })
        {
            await using var context = await NewContextAsync(GetBrowser(browser));
            var page = await OpenSessionAsync(context);
            var image = Convert.ToBase64String(ExactBoundaryFixture.Create(kind, copiedType));
            await RunCorpusCellAsync(page, "ldstr \"/tmp/boundary-source.dll\"\nldstr \"" + image + "\"\n"
                + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
            await TypeLineAsync(page, ".load /tmp/boundary-source.dll");
            await ExpectCompletionAsync(page, "types)");
            await RunCorpusCellAsync(page, "call int32 Owner::Read()\nret", 42);
            await TypeLineAsync(page, ".edit int32 Owner::Read() as Copy");
            await ExpectCompletionAsync(page, ".edit int32 Owner::Read() as Copy {");
            if (copiedType)
            {
                await page.Keyboard.PressAsync("Control+c");
                await TypeLineAsync(page, ".methods Copy");
                await ExpectCompletionAsync(page, "nominal");
                var transcript = (await BufferTextAsync(page)).Replace("│", "", StringComparison.Ordinal)
                    .Replace("▉", "", StringComparison.Ordinal);
                Assert.Contains("original nominal type", string.Join(' ', transcript.Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)));
                await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 43\nret\n}\n}",
                    "edit Copy committed as revision 1");
            }
            else
            {
                await page.Keyboard.PressAsync("Enter");
                await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
                await RunCorpusCellAsync(page, "call Copy\nret", 42);
                await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 43\nret\n}\n}",
                    "edit Copy committed as revision 2");
            }

            var parent = await ObserveComparisonResultsAsync(page);
            await TypeLineAsync(page, ".compare Copy ()");
            var original = await WaitForComparisonResultAsync(parent, 0);
            var edited = await WaitForComparisonResultAsync(parent, 1);
            Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
            Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
            Assert.AreEqual("42", original.GetProperty("result").GetProperty("value").GetString());
            Assert.AreEqual("43", edited.GetProperty("result").GetProperty("value").GetString());
            await ExpectComparisonTextAsync(page, "Copy: different");
            await RunCorpusCellAsync(page, "call Copy\nret", 43);
            Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        }
    }
}
