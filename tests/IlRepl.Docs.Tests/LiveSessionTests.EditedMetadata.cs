using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser method edits publish real explicit mappings and custom attributes through workers, revisions and saved assemblies.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Replacing a pinned slot changes real reflected dispatch and removing the directive restores that slot in fresh workers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the owner and interface use a constructed generic type.</param>
    /// <param name="slot">The interface or base slot to observe.</param>
    [TestMethod]
    [DataRow("chromium", false, "interface")]
    [DataRow("webkit", false, "interface")]
    [DataRow("chromium", false, "base")]
    [DataRow("webkit", false, "base")]
    [DataRow("chromium", true, "interface")]
    [DataRow("webkit", true, "interface")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonUsesEditedOverrideMappings(string browser, bool generic, string slot)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, EditedOverrideExamples.Source(generic) + "\n.edit "
            + EditedOverrideExamples.Reference(generic) + " as Copy {\n"
            + EditedOverrideExamples.Method(generic, "none") + "\n}\n" + EditedOverrideExamples.Scenario(generic, slot),
            "end of method Scenario");
        await RunCorpusCellAsync(page, "call Scenario\nldc.i4.s 100\nadd\nret", 142);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 0), "42", "43");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 1), "42", "43");
        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + EditedOverrideExamples.Method(generic, slot, repeat: true) + "\n}",
            "edit Copy committed as revision 2");
        await RunCorpusCellAsync(page, "call Scenario\nldc.i4 200\nadd\nret", 243);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 2), "42", "43");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 3), "43", "43");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Scenario");
        await TypeLineAsync(page, ".save /tmp/override-edit.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/override-edit.dll");
        await TypeLineAsync(page, ".clear");
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + EditedOverrideExamples.Method(generic, "none") + "\n}",
            "edit Copy committed as revision 3");
        await RunCorpusCellAsync(page, "call Scenario\nldc.i4 300\nadd\nret", 342);
        var originalOwner = "Owner" + (generic ? "<int32>" : "");
        var originalSlot = slot == "interface" ? "IValue<int32>" : "Base";
        await RunCorpusCellAsync(page, "newobj instance void " + originalOwner + "::.ctor()\ncallvirt instance int32 "
            + originalSlot + "::Read()\nldc.i4 400\nadd\nret", 442);
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/override-edit.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [override-edit]IlRepl.Cell::Run()\nunbox.any int32\nldc.i4 500\nadd\nret", 543);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// Typed attributes retain target scope, duplicates, named setters and type values through real browser worker reflection.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="isPrivate">Whether metadata must also reach the public callable forwarder.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonUsesEditedCustomAttributes(string browser, bool isPrivate)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, EditedCustomAttributeExamples.Source(isPrivate) + "\n.edit int32 Owner::Read(int32) as Copy {\n"
            + ".method " + (isPrivate ? "private" : "public") + " static int32 Read(int32 value) {\nldarg.0\nret\n}\n}\n"
            + EditedCustomAttributeExamples.Scenario(), "end of method Scenario");
        await RunCorpusCellAsync(page, "call Scenario\nret", 111);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 0), "111", "42");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 1), "111", "42");
        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + EditedCustomAttributeExamples.Method(isPrivate, edited: true) + "\n}",
            "edit Copy committed as revision 2");
        await RunCorpusCellAsync(page, "ldsfld int32 Tag::Runs\nldc.i4.s 100\nadd\nret", 100);
        await RunCorpusCellAsync(page, "call Scenario\nret", 423);
        await RunCorpusCellAsync(page, "ldsfld int32 Tag::Runs\nldc.i4 200\nadd\nret", 200);
        await RunCorpusCellAsync(page, """
            .locals init (object attribute)
            call Selected
            ldc.i4.0
            callvirt instance object[] MemberInfo::GetCustomAttributes(bool)
            ldc.i4.1
            ldelem.ref
            stloc.0
            ldloc.0
            callvirt instance class Type Object::GetType()
            ldstr "Name"
            callvirt instance class PropertyInfo Type::GetProperty(string)
            ldloc.0
            ldnull
            callvirt instance object PropertyInfo::GetValue(object, object[])
            castclass string
            ldstr "method"
            call bool String::op_Equality(string, string)
            ldc.i4.s 42
            mul
            ldc.i4 700
            add
            ret
            """, 742);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 2), "111", "42");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 3), "423", "42");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Scenario");
        await TypeLineAsync(page, ".save /tmp/custom-edit.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/custom-edit.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/custom-edit.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [custom-edit]IlRepl.Cell::Run()\nunbox.any int32\nldc.i4 500\nadd\nret", 923);
        await RunCorpusCellAsync(page, "call [custom-edit]IlRepl.Cell::Selected()\n"
            + "callvirt instance class ParameterInfo[] MethodBase::GetParameters()\nldc.i4.0\nldelem.ref\nldc.i4.0\n"
            + "callvirt instance object[] ParameterInfo::GetCustomAttributes(bool)\nldlen\nconv.i4\nldc.i4 600\nadd\nret", 603);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// Failed metadata submissions preserve the callable revision and allow a corrected source to commit normally.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="custom">Whether to reject a parameter attribute scope instead of an override target.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonMetadataEditsRecoverAfterInvalidSubmission(string browser, bool custom)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var source = custom ? EditedCustomAttributeExamples.Source(isPrivate: true) : EditedOverrideExamples.Source(false);
        var reference = custom ? "int32 Owner::Read(int32)" : EditedOverrideExamples.Reference(false);
        var method = custom ? EditedCustomAttributeExamples.Method(isPrivate: true, edited: true)
            : EditedOverrideExamples.Method(false, "interface");
        var scenario = custom ? EditedCustomAttributeExamples.Scenario() : EditedOverrideExamples.Scenario(false, "interface");
        await SubmitEditSourceAsync(page, source + "\n.edit " + reference + " as Copy {\n" + method + "\n}\n" + scenario,
            "end of method Scenario");
        var invalid = custom ? ".method private static int32 Read(int32 value) {\n.param [2]\nldc.i4.s 99\nret\n}"
            : EditedOverrideExamples.Method(false, "none").Replace("ldc.i4.s 43",
                ".override method instance class Type Object::GetType()\nldc.i4.s 99", StringComparison.Ordinal);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + invalid + "\n}", "incomplete on line");
        await PromptContainsAsync(page, "}");
        await ClearPromptAsync(page);
        await RunCorpusCellAsync(page, "call Scenario\nldc.i4 1000\nadd\nret", custom ? 1423 : 1043);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + method + "\n}", "edit Copy committed as revision 2");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 0), custom ? "111" : "42", custom ? "42" : "43");
        AssertEditedMetadataSide(await WaitForComparisonResultAsync(parent, 1), custom ? "423" : "43", custom ? "42" : "43");
        await ExpectComparisonTextAsync(page, "Copy: different");
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertEditedMetadataSide(JsonElement side, string expected, string selected)
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
        Assert.AreEqual(selected, returned.GetProperty("value").GetString(), json);
    }
}
