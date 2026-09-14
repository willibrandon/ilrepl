using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits retain non-IL declarations used only as reflection metadata.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Reflection enumerates runtime methods in unchanged edits, comparison workers, and saved assemblies.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="virtualMethod">Whether the declaration is virtual.</param>
    /// <param name="runtime">Whether the declaration uses the runtime code type.</param>
    [TestMethod]
    [DataRow("chromium", false, false)]
    [DataRow("webkit", false, false)]
    [DataRow("chromium", true, false)]
    [DataRow("webkit", true, false)]
    [DataRow("chromium", false, true)]
    [DataRow("webkit", false, true)]
    [DataRow("chromium", true, true)]
    [DataRow("webkit", true, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditRetainsReflectedRuntimeDeclarations(string browser, bool virtualMethod, bool runtime)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var image = Convert.ToBase64String(ReflectionRuntimeMetadataFixture.Create(virtualMethod, runtime));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/runtime-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/runtime-source.dll");
        await ExpectCompletionAsync(page, "types)");
        const string body = """
            .method public static int32 Read() {
              ldtoken Owner
              call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
              ldc.i4.s 46
              callvirt instance class MethodInfo[] Type::GetMethods(valuetype BindingFlags)
              ldlen
              conv.i4
              ret
            }
            """;
        await SubmitEditSourceAsync(page, ".edit int32 Owner::Read() as Copy {\n" + body + "\n}", "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call Copy\nret", 2);
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + body.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal)
            + "\n}", "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await ExpectComparisonTextAsync(page, "return: [System.Private.CoreLib]System.Int32 \"2\"");
        await ExpectComparisonTextAsync(page, "return: [System.Private.CoreLib]System.Int32 \"3\"");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/runtime-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/runtime-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/runtime-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [runtime-copy]IlRepl.Cell::Run()\nret", 3);
        const string member = """
            ldtoken [runtime-copy]IlRepl.Edits.Copy.Owner
            call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
            ldstr "Native"
            ldc.i4.s 44
            callvirt instance class MethodInfo Type::GetMethod(string, valuetype BindingFlags)
            """;
        await RunCorpusCellAsync(page, member
            + "\ncallvirt instance valuetype MethodImplAttributes MethodBase::GetMethodImplementationFlags()"
            + "\nconv.i4\nret", runtime ? 4099 : 4096);
        await RunCorpusCellAsync(page, member + "\ncallvirt instance bool MethodBase::get_IsVirtual()\nconv.i4\nret",
            virtualMethod ? 1 : 0);
        await RunCorpusCellAsync(page, member + "\ncallvirt instance class MethodBody MethodBase::GetMethodBody()\nldnull\nceq\nret", 1);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
