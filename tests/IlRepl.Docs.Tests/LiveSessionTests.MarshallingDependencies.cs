using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser exports retain session marshalers referenced only by native metadata descriptors.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// A copied custom marshaler can be created and used after saving and resetting the browser session.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="target">The field, parameter, or return descriptor.</param>
    [TestMethod]
    [DataRow("chromium", "field")]
    [DataRow("webkit", "field")]
    [DataRow("chromium", "parameter")]
    [DataRow("webkit", "parameter")]
    [DataRow("chromium", "return")]
    [DataRow("webkit", "return")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditCopiesDescriptorMarshaler(string browser, string target)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        page.Console += (_, message) => TestContext.WriteLine(message.Text);
        await SubmitEditSourceAsync(page, MarshallingDependencyExamples.Source, "end of class Marshaler");
        await RunCorpusCellAsync(page, """
            ldstr "MARSHAL-TYPE:"
            ldtoken Marshaler
            call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
            callvirt instance string Type::get_AssemblyQualifiedName()
            ldstr ":END-MARSHAL"
            call string String::Concat(string, string, string)
            call void Console::WriteLine(string)
            ldc.i4.1
            ret
            """, 1);
        var text = string.Join("", await BufferRowsAsync(page));
        const string prefix = "MARSHAL-TYPE:Marshaler,";
        var start = text.LastIndexOf(prefix, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, text);
        start += "MARSHAL-TYPE:".Length;
        var end = text.IndexOf(":END-MARSHAL", start, StringComparison.Ordinal);
        Assert.IsGreaterThan(start, end, text);
        var image = Convert.ToBase64String(MarshallingMetadataFixture.Create(text[start..end], target));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/marshal-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/marshal-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await SubmitEditSourceAsync(page, """
            .edit int32 Owner::Read(int32) as Copy {
              .method public static int32 Read(int32 value) cil managed {
                ldarg.0
                ldc.i4.1
                add
                ret
              }
            }
            """, "edit Copy committed as revision 1");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy (42)");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        Assert.AreEqual("42", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("43", edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "ldc.i4.s 42");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/marshal-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/marshal-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/marshal-copy.dll");
        await ExpectCompletionAsync(page, "public types)");
        await RunCorpusCellAsync(page, "call object [marshal-copy]IlRepl.Cell::Run()\nret", 43);
        var descriptor = target == "field"
            ? "ldtoken field object [marshal-copy]IlRepl.Edits.Copy.Owner::Value\n"
                + "call class FieldInfo FieldInfo::GetFieldFromHandle(valuetype RuntimeFieldHandle)\n"
            : "ldtoken method int32 [marshal-copy]IlRepl.Edits.Copy.Owner::Read(int32)\n"
                + "call class MethodBase MethodBase::GetMethodFromHandle(valuetype RuntimeMethodHandle)\n"
                + (target == "return" ? "castclass MethodInfo\ncallvirt instance class ParameterInfo MethodInfo::get_ReturnParameter()\n"
                    : "callvirt instance class ParameterInfo[] MethodBase::GetParameters()\nldc.i4.0\nldelem.ref\n");
        descriptor += "ldtoken System.Runtime.InteropServices.MarshalAsAttribute\n"
            + "call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n"
            + "call class Attribute Attribute::GetCustomAttribute(class " + (target == "field" ? "MemberInfo" : "ParameterInfo")
            + ", class Type)\ncastclass System.Runtime.InteropServices.MarshalAsAttribute\n"
            + "ldfld class Type System.Runtime.InteropServices.MarshalAsAttribute::MarshalTypeRef\n";
        await RunCorpusCellAsync(page, descriptor + """
            ldstr "GetInstance"
            callvirt instance class MethodInfo Type::GetMethod(string)
            ldnull
            ldc.i4.1
            newarr object
            dup
            ldc.i4.0
            ldstr "saved cookie"
            stelem.ref
            callvirt instance object MethodBase::Invoke(object, object[])
            castclass System.Runtime.InteropServices.ICustomMarshaler
            callvirt instance int32 System.Runtime.InteropServices.ICustomMarshaler::GetNativeDataSize()
            ret
            """, 42);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
