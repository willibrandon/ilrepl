using System.Globalization;
using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits retain explicit parameter defaults through aliases, reflection, worker comparisons, and exports.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Revised defaults reach actual calls and independent workers while preserving the original parameter metadata.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="originalDefault">The original constant, or null when none exists.</param>
    /// <param name="isPrivate">Whether the alias forwards to a private target.</param>
    /// <param name="optional">Whether the edited parameter keeps the Optional flag.</param>
    [TestMethod]
    [DataRow("chromium", 7, false, true)]
    [DataRow("webkit", 7, false, true)]
    [DataRow("chromium", 7, true, false)]
    [DataRow("webkit", 7, true, false)]
    [DataRow("chromium", null, true, true)]
    [DataRow("webkit", null, true, true)]
    [DataRow("chromium", null, false, false)]
    [DataRow("webkit", null, false, false)]
    [DoNotParallelize]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ParameterDefaultComparisonPreservesExplicitConstants(string browser, int? originalDefault,
        bool isPrivate, bool optional)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        await SubmitEditSourceAsync(page, ParameterDefaultComparisonExamples.Source(originalDefault, isPrivate, optional: true)
            + "\n.edit int32 Owner::Read(int32) as Copy {\n"
            + ParameterDefaultComparisonExamples.Method(8, isPrivate, optional, duplicate: isPrivate) + "\n}\n"
            + ParameterDefaultComparisonExamples.Scenarios(), "end of method MissingScenario");
        const string originalParameter = "ldtoken Owner\n"
            + "call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\nldstr \"Read\"\nldc.i4.s 56\n"
            + "callvirt instance class MethodInfo Type::GetMethod(string, valuetype BindingFlags)\n"
            + "callvirt instance class ParameterInfo[] MethodBase::GetParameters()\nldc.i4.0\nldelem.ref\n";
        await RunCorpusCellAsync(page, originalParameter
            + "callvirt instance bool ParameterInfo::get_HasDefaultValue()\nconv.i4\nret", originalDefault.HasValue ? 1 : 0);
        if (originalDefault is { } original)
            await RunCorpusCellAsync(page, originalParameter
                + "callvirt instance object ParameterInfo::get_RawDefaultValue()\nunbox.any int32\nret", original);
        await RunCorpusCellAsync(page, "call Parameter\n"
            + "callvirt instance object ParameterInfo::get_RawDefaultValue()\nunbox.any int32\nret", 8);
        await RunCorpusCellAsync(page, "call Parameter\n"
            + "callvirt instance valuetype ParameterAttributes ParameterInfo::get_Attributes()\nconv.i4\nret", 4096 | (optional ? 16 : 0));
        await RunCorpusCellAsync(page, "call MissingScenario\nret", 8);

        await TypeLineAsync(page, ".compare Copy using Scenario");
        var before = await WaitForComparisonResultAsync(parent, 0);
        var after = await WaitForComparisonResultAsync(parent, 1);
        AssertParameterDefaultSide(before, (originalDefault ?? -1).ToString(CultureInfo.InvariantCulture), "42");
        AssertParameterDefaultSide(after, "8", "42");
        await ExpectComparisonTextAsync(page, "edited: completed");
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await InputIdleAsync(page);

        await TypeLineAsync(page, ".compare Copy using MissingScenario");
        before = await WaitForComparisonResultAsync(parent, 2);
        after = await WaitForComparisonResultAsync(parent, 3);
        AssertParameterDefaultSide(before, "7", "7");
        AssertParameterDefaultSide(after, "8", "8");
        await ExpectComparisonTextAsync(page, "Copy: different-inputs");
        Assert.Contains("Copy: different-inputs", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await InputIdleAsync(page);

        await TypeLineAsync(page, "call MissingScenario");
        await TypeLineAsync(page, ".save /tmp/parameter-default.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/parameter-default.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/parameter-default.dll");
        await ExpectCompletionAsync(page, "public types)");
        await RunCorpusCellAsync(page, "call object [parameter-default]IlRepl.Cell::Run()\nret", 8);
        await RunCorpusCellAsync(page, "call [parameter-default]IlRepl.Cell::Parameter()\n"
            + "callvirt instance object ParameterInfo::get_RawDefaultValue()\nunbox.any int32\nret", 8);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertParameterDefaultSide(JsonElement side, string expected, string argument)
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
        var input = invocation.GetProperty("inputs").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "argument 0").GetProperty("value");
        Assert.AreEqual("scalar", input.GetProperty("kind").GetString(), json);
        Assert.AreEqual(argument, input.GetProperty("value").GetString(), json);
        var outputs = invocation.GetProperty("outputs").EnumerateArray().ToArray();
        foreach (var name in new[] { "argument 0", "return" })
        {
            var output = outputs.Single(member => member.GetProperty("name").GetString() == name).GetProperty("value");
            Assert.AreEqual("scalar", output.GetProperty("kind").GetString(), json);
            Assert.AreEqual(argument, output.GetProperty("value").GetString(), json);
        }
    }
}
