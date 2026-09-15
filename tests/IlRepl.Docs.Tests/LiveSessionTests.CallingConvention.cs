using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Removing managed varargs produces a normal method that the browser can execute and save.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// The edited header changes the reflected convention and survives execution from an exported copy.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditRemovesTheVarargConvention(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(CallingConventionExamples.Create(true));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/convention.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/convention.dll");
        await ExpectCompletionAsync(page, "types)");
        await SubmitEditSourceAsync(page, ".edit vararg int32 Owner::Read() as Copy {\n"
            + CallingConventionExamples.Method(false) + "\n}", "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, """
            ldtoken method int32 IlRepl.Edits.Copy.Owner::Read()
            call class MethodBase MethodBase::GetMethodFromHandle(valuetype RuntimeMethodHandle)
            callvirt instance valuetype CallingConventions MethodBase::get_CallingConvention()
            conv.i4
            ret
            """, 1);
        await RunCorpusCellAsync(page, "call Copy\nret", 42);
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/convention-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/convention-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/convention-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [convention-copy]IlRepl.Cell::Run()\nret", 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
