using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Real browser-loaded public helper assemblies preserve shared copied state and independent external helper identity.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Original and copied helper graphs agree before editing and remain independently executable after revision and export.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="shape">The selected helper state, callback, generic, initializer, lookup or retained identity route.</param>
    [TestMethod]
    [DataRow("chromium", "read")]
    [DataRow("webkit", "read")]
    [DataRow("chromium", "write")]
    [DataRow("webkit", "write")]
    [DataRow("chromium", "callback")]
    [DataRow("webkit", "callback")]
    [DataRow("chromium", "generic")]
    [DataRow("webkit", "generic")]
    [DataRow("chromium", "cctor")]
    [DataRow("webkit", "cctor")]
    [DataRow("chromium", "revisit")]
    [DataRow("webkit", "revisit")]
    [DataRow("chromium", "identity")]
    [DataRow("webkit", "identity")]
    [DataRow("chromium", "lookup")]
    [DataRow("webkit", "lookup")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesPublicSourceHelperContext(string browser, string shape)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(PublicHelperFixture.Create(shape));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/public-helper-source.dll\"\nldstr \"" + image + "\"\n"
            + "call unsigned int8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, unsigned int8[])\n"
            + "ldc.i4.s 97\nret", 97);
        await TypeLineAsync(page, ".load /tmp/public-helper-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 PublicContext.Owner::Read()\nldc.i4.s 100\nadd\nret", 142);
        await RunCorpusCellAsync(page, "ldc.i4.7\nstsfld int32 PublicContext.Owner::State\nldc.i4.s 17\n"
            + "stsfld int32 PublicContext.Helper::Cached\nldsfld int32 PublicContext.Owner::State\nldc.i4 200\nadd\nret", 207);
        await TypeLineAsync(page, ".edit int32 PublicContext.Owner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        var history = await StoredHistoryAsync(page);
        var submitted = history.Last(entry => entry.StartsWith(".edit ", StringComparison.Ordinal));
        var source = ".edit Copy " + submitted[submitted.IndexOf('{')..];
        await SubmitEditSourceAsync(page, PublicHelperFixture.Scenario(), "end of method Scenario");
        await RunCorpusCellAsync(page, "call Copy\nldc.i4 300\nadd\nret", 342);
        await RunCorpusCellAsync(page, "ldsfld int32 PublicContext.Owner::State\nldc.i4 400\nadd\nret", 407);
        await RunCorpusCellAsync(page, "ldsfld int32 PublicContext.Helper::Cached\nldc.i4 500\nadd\nret", 517);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        AssertPublicHelperSide(await WaitForComparisonResultAsync(parent, 0), "42");
        AssertPublicHelperSide(await WaitForComparisonResultAsync(parent, 1), "42");
        await ExpectComparisonTextAsync(page, "Copy: match");
        var before = shape == "write" ? "ldc.i4.s 41" : "ldc.i4.s 42";
        var after = shape == "write" ? "ldc.i4.s 42" : "ldc.i4.s 43";
        Assert.Contains(before, source);
        await SubmitEditSourceAsync(page, source.Replace(before, after, StringComparison.Ordinal), "edit Copy committed as revision 2");
        await RunCorpusCellAsync(page, "call Copy\nldc.i4 600\nadd\nret", 643);
        await RunCorpusCellAsync(page, "ldsfld int32 PublicContext.Owner::State\nldc.i4 700\nadd\nret", 707);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        AssertPublicHelperSide(await WaitForComparisonResultAsync(parent, 2), "42");
        AssertPublicHelperSide(await WaitForComparisonResultAsync(parent, 3), "43");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/public-helper-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/public-helper-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        if (shape == "identity")
        {
            await TypeLineAsync(page, ".load /tmp/public-helper-source.dll");
            await ExpectCompletionAsync(page, "types)");
        }
        await TypeLineAsync(page, ".load /tmp/public-helper-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [public-helper-copy]IlRepl.Cell::Run()\nunbox.any int32\nldc.i4 800\nadd\nret", 843);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertPublicHelperSide(JsonElement side, string expected)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var result), json);
        Assert.AreEqual(JsonValueKind.Object, result.ValueKind, json);
        Assert.AreEqual("scalar", result.GetProperty("kind").GetString(), json);
        Assert.AreEqual(expected, result.GetProperty("value").GetString(), json);
        var invocations = side.GetProperty("invocations");
        Assert.AreEqual(1, invocations.GetArrayLength(), json);
        var invocation = invocations[0];
        Assert.IsFalse(invocation.TryGetProperty("exception", out exception) && exception.ValueKind != JsonValueKind.Null, json);
        var returned = invocation.GetProperty("outputs").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "return").GetProperty("value");
        Assert.AreEqual("scalar", returned.GetProperty("kind").GetString(), json);
        Assert.AreEqual(expected, returned.GetProperty("value").GetString(), json);
        var inputs = invocation.GetProperty("inputs").EnumerateArray().ToArray();
        Assert.AreEqual("null", inputs.Single(member => member.GetProperty("name").GetString() == "receiver")
            .GetProperty("value").GetProperty("kind").GetString(), json);
        Assert.DoesNotContain(member => member.GetProperty("name").GetString()!.StartsWith("argument ", StringComparison.Ordinal), inputs);
    }
}
