namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser comparisons load only the available libraries needed by the executed path.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Unused missing references allow normal execution, and actual calls to them report the worker's load exception.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="useMissing">Whether the chosen method calls the absent library.</param>
    /// <returns>The completed capture and worker assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonAllowsUnusedMissingLibrary(string browser, bool useMissing)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(OptionalDependencyImage.Create());
        await RunCorpusCellAsync(page, "ldstr \"/tmp/optional.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void System.IO.File::WriteAllBytes(string, uint8[])\n"
            + "ldc.i4.s 42\nret", 42);
        await TypeLineAsync(page, ".load /tmp/optional.dll");
        await ExpectCompletionAsync(page, "public types)");
        var target = useMissing ? "Optional" : "Read";
        await SubmitEditSourceAsync(page, ".method int32 Work() {\ncall int32 [OptionalLibrary]N.Library::" + target + "()\nret\n}\n"
            + ".edit Work as Copy {\n.method public static int32 Work() cil managed {\n"
            + "call int32 [OptionalLibrary]N.Library::" + target + "()\nldc.i4.1\nadd\nret\n}\n}",
            "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            if (useMissing)
            {
                var exception = side.GetProperty("invocations")[0].GetProperty("exception");
                Assert.EndsWith("FileNotFoundException", exception.GetProperty("type").GetString());
                var file = exception.GetProperty("fields").EnumerateArray()
                    .Single(member => member.GetProperty("name").GetString()!.Contains("FileName", StringComparison.Ordinal));
                var fileName = file.GetProperty("value").GetProperty("value").GetString();
                Assert.IsNotNull(fileName);
                Assert.Contains("MissingOptionalLibrary", fileName);
            }
        }

        await ExpectComparisonTextAsync(page, useMissing ? "FileNotFoundException" : "Copy: different");
        await InputIdleAsync(page);
        var text = await ReadComparisonTranscriptAsync(page);
        Assert.Contains(useMissing ? "Copy: match" : "Copy: different", text);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        if (useMissing)
        {
            Assert.Contains("FileNotFoundException", text);
            Assert.Contains("MissingOptionalLibrary", text);
        }
        else
        {
            Assert.Contains("\"41\"", text);
            Assert.Contains("\"42\"", text);
        }

        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
