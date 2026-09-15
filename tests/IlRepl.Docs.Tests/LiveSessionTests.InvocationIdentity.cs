using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser worker comparisons preserve references between entry and exit even when replacements have identical values.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Real ReferenceEquals witnesses and independent workers distinguish every supported replacement path across one invocation.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="shape">The reference path or delayed completion to observe.</param>
    [TestMethod]
    [DataRow("chromium", "ref-object")]
    [DataRow("webkit", "ref-object")]
    [DataRow("chromium", "ref-string")]
    [DataRow("webkit", "ref-string")]
    [DataRow("chromium", "ref-box")]
    [DataRow("webkit", "ref-box")]
    [DataRow("chromium", "receiver")]
    [DataRow("webkit", "receiver")]
    [DataRow("chromium", "array")]
    [DataRow("webkit", "array")]
    [DataRow("chromium", "dictionary")]
    [DataRow("webkit", "dictionary")]
    [DataRow("chromium", "return")]
    [DataRow("webkit", "return")]
    [DataRow("chromium", "task")]
    [DataRow("webkit", "task")]
    [DataRow("chromium", "valuetask")]
    [DataRow("webkit", "valuetask")]
    [DataRow("chromium", "throw")]
    [DataRow("webkit", "throw")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesIdentityAcrossInvocationBoundaries(string browser, string shape)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var reference = InvocationIdentityExamples.Reference(shape);
        var scenarios = InvocationIdentityExamples.Scenarios(shape);
        var originals = scenarios.Replace("IlRepl.Edits.Copy.Owner", "Owner", StringComparison.Ordinal)
            .Replace("call Copy", "call " + reference, StringComparison.Ordinal)
            .Replace("int32 Scenario()", "int32 OriginalScenario()", StringComparison.Ordinal)
            .Replace("int32 Witness()", "int32 OriginalWitness()", StringComparison.Ordinal);
        await SubmitEditSourceAsync(page, InvocationIdentityExamples.Source(shape) + "\n.edit " + reference + " as Copy {\n"
            + InvocationIdentityExamples.Method(shape, replace: false) + "\n}\n" + scenarios + "\n" + originals,
            "end of method OriginalWitness");
        await RunCorpusCellAsync(page, "call OriginalWitness\nldc.i4.s 100\nadd\nret", 101);
        await RunCorpusCellAsync(page, "call Witness\nldc.i4 200\nadd\nret", 201);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        AssertInvocationIdentitySide(await WaitForComparisonResultAsync(parent, 0), shape, replaced: false);
        AssertInvocationIdentitySide(await WaitForComparisonResultAsync(parent, 1), shape, replaced: false);
        await InputIdleAsync(page);
        Assert.Contains("Copy: match", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await InputIdleAsync(page);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + InvocationIdentityExamples.Method(shape, replace: true) + "\n}",
            "edit Copy committed as revision 2");
        await RunCorpusCellAsync(page, "call Witness\nldc.i4 300\nadd\nret", 300);
        await RunCorpusCellAsync(page, "call OriginalWitness\nldc.i4 400\nadd\nret", 401);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        AssertInvocationIdentitySide(await WaitForComparisonResultAsync(parent, 2), shape, replaced: false);
        AssertInvocationIdentitySide(await WaitForComparisonResultAsync(parent, 3), shape, replaced: true);
        await InputIdleAsync(page);
        Assert.Contains("Copy: different", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await InputIdleAsync(page);
        await TypeLineAsync(page, "call Witness");
        await TypeLineAsync(page, ".save /tmp/invocation-identity.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/invocation-identity.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/invocation-identity.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [invocation-identity]IlRepl.Cell::Run()\nunbox.any int32\nldc.i4 500\nadd\nret", 500);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertInvocationIdentitySide(JsonElement side, string shape, bool replaced)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var result), json);
        Assert.AreEqual(JsonValueKind.Object, result.ValueKind, json);
        Assert.AreEqual("scalar", result.GetProperty("kind").GetString(), json);
        Assert.AreEqual("42", result.GetProperty("value").GetString(), json);
        var invocations = side.GetProperty("invocations");
        Assert.AreEqual(1, invocations.GetArrayLength(), json);
        var invocation = invocations[0];
        var name = shape == "receiver" ? "receiver" : "argument 0";
        var input = InvocationIdentityRoot(invocation.GetProperty("inputs"), name);
        var output = InvocationIdentityRoot(invocation.GetProperty("outputs"), name);
        if (shape is "receiver" or "array" or "dictionary")
        {
            Assert.AreEqual(input.GetProperty("identity").GetInt32(), output.GetProperty("identity").GetInt32(), json);
            input = InvocationIdentityNested(input, shape);
            output = InvocationIdentityNested(output, shape);
        }
        if (shape is "return" or "task" or "valuetask")
        {
            Assert.AreEqual(input.GetProperty("identity").GetInt32(), output.GetProperty("identity").GetInt32(), json);
            Assert.AreEqual("object", output.GetProperty("kind").GetString(), json);
            Assert.AreEqual("42", InvocationIdentityField(output, "Number").GetProperty("value").GetString(), json);
            output = InvocationIdentityRoot(invocation.GetProperty("outputs"), "return");
        }
        if (shape == "throw")
        {
            Assert.IsTrue(invocation.TryGetProperty("exception", out exception), json);
            Assert.AreEqual("same exception", exception.GetProperty("message").GetString(), json);
            if (replaced) Assert.AreNotEqual(input.GetProperty("identity").GetInt32(), exception.GetProperty("identity").GetInt32(), json);
            else Assert.AreEqual(input.GetProperty("identity").GetInt32(), exception.GetProperty("identity").GetInt32(), json);
            return;
        }
        Assert.IsFalse(invocation.TryGetProperty("exception", out exception) && exception.ValueKind != JsonValueKind.Null, json);
        if (replaced) Assert.AreNotEqual(input.GetProperty("identity").GetInt32(), output.GetProperty("identity").GetInt32(), json);
        else Assert.AreEqual(input.GetProperty("identity").GetInt32(), output.GetProperty("identity").GetInt32(), json);
        if (shape is "ref-string" or "ref-box")
        {
            Assert.AreEqual("scalar", input.GetProperty("kind").GetString(), json);
            Assert.AreEqual("scalar", output.GetProperty("kind").GetString(), json);
            Assert.AreEqual(shape == "ref-string" ? "x" : "42", input.GetProperty("value").GetString(), json);
            Assert.AreEqual(input.GetProperty("value").GetString(), output.GetProperty("value").GetString(), json);
        }
        else
        {
            Assert.AreEqual("object", input.GetProperty("kind").GetString(), json);
            Assert.AreEqual("42", InvocationIdentityField(input, "Number").GetProperty("value").GetString(), json);
            if (shape is "return" or "task" or "valuetask" && !replaced)
            {
                Assert.AreEqual("reference", output.GetProperty("kind").GetString(), json);
                Assert.AreEqual(0, output.GetProperty("members").GetArrayLength(), json);
            }
            else
            {
                Assert.AreEqual("object", output.GetProperty("kind").GetString(), json);
                Assert.AreEqual("42", InvocationIdentityField(output, "Number").GetProperty("value").GetString(), json);
            }
        }
    }

    private static JsonElement InvocationIdentityRoot(JsonElement members, string name) =>
        members.EnumerateArray().Single(member => member.GetProperty("name").GetString() == name).GetProperty("value");

    private static JsonElement InvocationIdentityField(JsonElement value, string name) => value.GetProperty("members").EnumerateArray()
        .Single(member => member.GetProperty("name").GetString()!.EndsWith("::" + name, StringComparison.Ordinal)).GetProperty("value");

    private static JsonElement InvocationIdentityNested(JsonElement value, string shape) => shape switch
    {
        "receiver" => InvocationIdentityField(value, "Value"),
        "array" => value.GetProperty("members")[0].GetProperty("value").GetProperty("members")[0].GetProperty("value"),
        _ => InvocationIdentityRoot(value.GetProperty("members").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "0").GetProperty("value").GetProperty("members"), "value"),
    };
}
