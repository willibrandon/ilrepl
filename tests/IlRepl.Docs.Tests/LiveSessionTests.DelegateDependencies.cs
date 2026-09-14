using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser copies retain private runtime delegates and execute their copied targets after export.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A copied delegate keeps its constructor, invocation, and target through comparison and save/reload.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="generic">Whether the private delegate has a generic result type.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditCopiesRuntimeDelegate(string browser, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        page.Console += (_, message) => TestContext.WriteLine(message.Text);
        var image = Convert.ToBase64String(DelegateMetadataFixture.Create(generic));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/delegate-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/delegate-source.dll");
        await ExpectCompletionAsync(page, "types)");
        var callback = "Owner/Callback" + (generic ? "`1<int32>" : "");
        await SubmitEditSourceAsync(page, ".edit int32 Owner::Read() as Copy {\n.method public static int32 Read() {\n"
            + "ldnull\nldftn int32 Owner::Target()\nnewobj instance void " + callback + "::.ctor(object, native int)\n"
            + "dup\nstsfld class " + callback + " Owner::Last\ncallvirt instance int32 " + callback + "::Invoke()\n"
            + "ldc.i4.1\nadd\nret\n}\n}", "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        Assert.AreEqual("42", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("43", edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/delegate-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/delegate-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/delegate-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [delegate-copy]IlRepl.Cell::Run()\nret", 43);
        await RunCorpusCellAsync(page, """
            ldtoken [delegate-copy]IlRepl.Edits.Copy.Owner
            call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
            ldstr "Last"
            callvirt instance class FieldInfo Type::GetField(string)
            ldnull
            callvirt instance object FieldInfo::GetValue(object)
            castclass Delegate
            ldnull
            callvirt instance object Delegate::DynamicInvoke(object[])
            ret
            """, 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
