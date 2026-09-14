using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser exports retain attribute constructors, typed arguments, and named properties from separate session declarations.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A saved edit loads independently and its copied attribute can be constructed and read through reflection.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed comparison, saved assembly, and attribute construction assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditExportsAttributeDependencies(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, AttributeDependencyExamples.Source() + """

            .edit int32 Tagged::Read(int32) as Copy {
              .method public static int32 Read(int32 value) cil managed {
                ldarg.0
                ldc.i4.1
                add
                ret
              }
            }
            """, "edit Copy committed as revision 1");
        await TypeLineAsync(page, ".compare Copy (41)");
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, ".save /tmp/attribute-types.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/attribute-types.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/attribute-types.dll");
        await ExpectCompletionAsync(page, "public types)");
        await RunCorpusCellAsync(page, """
            .locals init (object attribute)
            ldtoken [attribute-types]IlRepl.Edits.Copy.Owner
            call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
            ldc.i4.0
            callvirt instance object[] System.Reflection.MemberInfo::GetCustomAttributes(bool)
            ldc.i4.0
            ldelem.ref
            stloc.0
            ldloc.0
            callvirt instance class Type Object::GetType()
            ldstr "Name"
            callvirt instance class System.Reflection.PropertyInfo Type::GetProperty(string)
            ldloc.0
            ldnull
            callvirt instance object System.Reflection.PropertyInfo::GetValue(object, object[])
            castclass string
            ldstr "saved"
            call bool String::op_Equality(string, string)
            ldc.i4.s 42
            mul
            ret
            """, 42);
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }
}
