namespace IlRepl.Docs.Tests;

/// <summary>
/// Quoted method names remain usable in browser edits with automatic and explicit names.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// An alias word inside a quoted identifier survives editing, comparison, and saved assembly execution.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="explicitAlias">Whether to supply a name for the copy.</param>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditQuotedAliasWords(string browser, bool explicitAlias)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var name = explicitAlias ? "Copy" : "Read_as_Text_Edit";
        await SubmitEditSourceAsync(page, """
            .class public 'Owner as Type' {
              .method public static int32 'Read as Text'() {
                ldc.i4.s 42
                ret
              }
            }
            """ + "\n.edit int32 'Owner as Type'::'Read as Text'()" + (explicitAlias ? " as Copy" : "") + " {\n"
            + ".method public static int32 'Read as Text'() cil managed {\nldc.i4.s 43\nret\n}\n}",
            "edit " + name + " committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare " + name + " ()");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        Assert.AreEqual("42", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("43", edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, name + ": different");
        await TypeLineAsync(page, "call " + name);
        await TypeLineAsync(page, ".save /tmp/quoted-edit.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/quoted-edit.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/quoted-edit.dll");
        await ExpectCompletionAsync(page, "public types)");
        await RunCorpusCellAsync(page, "call object [quoted-edit]IlRepl.Cell::Run()\nret", 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
