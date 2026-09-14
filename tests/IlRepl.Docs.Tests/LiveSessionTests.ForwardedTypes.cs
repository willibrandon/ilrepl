using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// A real assembly forwarder remains observable to the original and receives a precise copied-context blocker.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Forwarder enumeration is blocked before execution and a corrected draft compares and exports normally.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditRejectsForwardedTypeEnumeration(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(ForwardedTypeFixture.Create());
        await RunCorpusCellAsync(page, "ldstr \"/tmp/forwarders.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/forwarders.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 Owner::Read()\nret", 1);
        await SubmitEditSourceAsync(page, """
            .edit int32 Owner::Read() as Copy {
              .method public static int32 Read() {
                call class Assembly Assembly::GetExecutingAssembly()
                callvirt instance class Type[] Assembly::GetForwardedTypes()
                ldlen
                conv.i4
                ret
              }
            }
            """, "complete type set");
        await PromptContainsAsync(page, "}");
        await ClearPromptAsync(page);
        await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read() {\nldc.i4.1\nret\n}\n}",
            "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("1", side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/forwarder-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/forwarder-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/forwarder-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [forwarder-copy]IlRepl.Cell::Run()\nret", 1);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
