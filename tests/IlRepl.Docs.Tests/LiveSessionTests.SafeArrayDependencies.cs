using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser exports preserve SAFEARRAY descriptors with copied private subtype identities.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Saved subtype strings resolve to the copied assembly after a reset, including generic and array shapes.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="target">The field, parameter, or return descriptor.</param>
    /// <param name="generic">Whether the subtype includes a generic collection and an array.</param>
    [TestMethod]
    [DataRow("chromium", "field", false)]
    [DataRow("webkit", "field", false)]
    [DataRow("chromium", "parameter", false)]
    [DataRow("webkit", "parameter", false)]
    [DataRow("chromium", "return", false)]
    [DataRow("webkit", "return", false)]
    [DataRow("chromium", "field", true)]
    [DataRow("webkit", "field", true)]
    [DataRow("chromium", "parameter", true)]
    [DataRow("webkit", "parameter", true)]
    [DataRow("chromium", "return", true)]
    [DataRow("webkit", "return", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditCopiesSafeArraySubtype(string browser, string target, bool generic)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        page.Console += (_, message) => TestContext.WriteLine(message.Text);
        var image = Convert.ToBase64String(SafeArrayMetadataFixture.Create(target, generic));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/array-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/array-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await SubmitEditSourceAsync(page, """
            .edit object[] Owner::Read(object[]) as Copy {
              .method public static object[] Read(object[] items) {
                ldc.i4.3
                newarr object
                ret
              }
            }
            .method int32 Scenario() {
              ldc.i4.2
              newarr object
              call Copy
              ldlen
              conv.i4
              ret
            }
            """ + "\n" + SavedSafeArraySubtype(target), "end of method SavedSubtype");
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        var original = await WaitForComparisonResultAsync(parent, 0);
        var edited = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual("completed", original.GetProperty("outcome").GetString(), original.GetRawText());
        Assert.AreEqual("completed", edited.GetProperty("outcome").GetString(), edited.GetRawText());
        Assert.AreEqual("2", original.GetProperty("result").GetProperty("value").GetString());
        Assert.AreEqual("3", edited.GetProperty("result").GetProperty("value").GetString());
        await ExpectComparisonTextAsync(page, "edited: completed");
        await TypeLineAsync(page, "call Scenario");
        await TypeLineAsync(page, ".save /tmp/safe-array.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/safe-array.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/safe-array.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [safe-array]IlRepl.Cell::Run()\nret", 3);
        var subtype = "call class Type [safe-array]IlRepl.Cell::SavedSubtype()\n" + (generic
            ? "callvirt instance class Type[] Type::GetGenericArguments()\nldc.i4.0\nldelem.ref\n"
                + "callvirt instance class Type Type::GetElementType()\n" : "");
        await RunCorpusCellAsync(page, subtype + """
            callvirt instance class Assembly Type::get_Assembly()
            ldtoken [safe-array]IlRepl.Edits.Copy.Owner
            call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
            callvirt instance class Assembly Type::get_Assembly()
            call bool Object::ReferenceEquals(object, object)
            conv.i4
            ret
            """, 1);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static string SavedSafeArraySubtype(string target)
    {
        const string metadata = "System.Reflection.Metadata.";
        const string peReader = "class System.Reflection.PortableExecutable.PEReader";
        const string reader = "class " + metadata + "MetadataReader";
        var kind = target == "field" ? "FieldDefinition" : "Parameter";
        var row = "valuetype " + metadata + kind;
        var handle = row + "Handle";
        const string blob = "valuetype " + metadata + "BlobReader";
        return ".method class Type SavedSubtype() {\n.locals init (" + peReader + " pe, " + reader + " metadata, "
            + row + " row, valuetype " + metadata + "BlobHandle descriptor, " + blob + " blob, int32 index)\n"
            + "ldstr \"/tmp/safe-array.dll\"\ncall class FileStream File::OpenRead(string)\nnewobj instance void "
            + peReader + "::.ctor(class Stream)\ndup\nstloc.0\ncall " + reader + " " + metadata
            + "PEReaderExtensions::GetMetadataReader(" + peReader + ")\nstloc.1\nNEXT: ldloc.1\n"
            + "ldloc.s 5\nldc.i4.1\nadd\ndup\nstloc.s 5\ncall " + handle + " "
            + metadata + "Ecma335.MetadataTokens::" + kind + "Handle(int32)\ncallvirt instance " + row + " " + reader + "::Get"
            + kind + "(" + handle + ")\nstloc.2\nldloca.s 2\ncall instance valuetype " + metadata + "BlobHandle " + row
            + "::GetMarshallingDescriptor()\nstloc.3\nldloca.s 3\ncall instance bool valuetype " + metadata
            + "BlobHandle::get_IsNil()\nbrtrue NEXT\nldloc.1\nldloc.3\ncallvirt instance " + blob + " " + reader
            + "::GetBlobReader(valuetype " + metadata + "BlobHandle)\nstloc.s 4\nldloca.s 4\ncall instance uint8 "
            + blob + "::ReadByte()\npop\nldloca.s 4\ncall instance int32 " + blob + "::ReadCompressedInteger()\npop\n"
            + "ldloca.s 4\ncall instance string " + blob + "::ReadSerializedString()\ncall class Type Type::GetType(string)\n"
            + "ldloc.0\ncallvirt instance void IDisposable::Dispose()\nret\n}";
    }
}
