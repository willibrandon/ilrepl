using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser edits and saved assemblies retain property and event other-accessor associations.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Other accessors remain in saved metadata with their executable bodies and attribute dependencies.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="eventMember">Whether the association belongs to an event.</param>
    /// <param name="standard">Whether standard accessors and another other accessor are also retained.</param>
    [TestMethod]
    [DataRow("chromium", false, false)]
    [DataRow("webkit", false, false)]
    [DataRow("chromium", false, true)]
    [DataRow("webkit", false, true)]
    [DataRow("chromium", true, false)]
    [DataRow("webkit", true, false)]
    [DataRow("chromium", true, true)]
    [DataRow("webkit", true, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesOtherAccessors(string browser, bool eventMember, bool standard)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        page.Console += (_, message) => TestContext.WriteLine(message.Text);
        var image = Convert.ToBase64String(AccessorMetadataFixture.Create(eventMember, standard, standard, privateAccessor: false));
        await RunCorpusCellAsync(page, "ldstr \"/tmp/accessors.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/accessors.dll");
        await ExpectCompletionAsync(page, "public types)");
        var body = standard ? eventMember
            ? "ldnull\ncall void Owner::add_Changed(class Action)\nldnull\ncall void Owner::remove_Changed(class Action)\n"
                + "call void Owner::Raise()\n"
            : "ldnull\ncall void Owner::set_Value(class Owner/Marker)\ncall class Owner/Marker Owner::get_Value()\npop\n" : "";
        if (standard)
        {
            body += "call int32 Owner::Extra()\npop\n";
        }

        await SubmitEditSourceAsync(page, ".edit int32 Owner::Read() as Copy {\n.method public static int32 Read() cil managed {\n"
            + body + "ldc.i4.s 43\nret\n}\n}\n" + AccessorCountMethod(eventMember), "end of method AccessorCount");
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
        await TypeLineAsync(page, ".save /tmp/other-accessors.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/other-accessors.dll");
        await TypeLineAsync(page, ".clear");
        await RunCorpusCellAsync(page, "call AccessorCount\nret", standard ? 2 : 1);
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/other-accessors.dll");
        await ExpectCompletionAsync(page, "public types)");
        await RunCorpusCellAsync(page, "call object [other-accessors]IlRepl.Cell::Run()\nret", 43);
        await RunCorpusCellAsync(page, "call int32 [other-accessors]IlRepl.Cell::AccessorCount()\nret", standard ? 2 : 1);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static string AccessorCountMethod(bool eventMember)
    {
        const string metadata = "System.Reflection.Metadata.";
        const string peReader = "class System.Reflection.PortableExecutable.PEReader";
        const string reader = "class " + metadata + "MetadataReader";
        var kind = eventMember ? "Event" : "Property";
        var definition = "valuetype " + metadata + kind + "Definition";
        var handle = definition + "Handle";
        var accessors = "valuetype " + metadata + kind + "Accessors";
        const string methods = "valuetype System.Collections.Immutable.ImmutableArray`1<valuetype "
            + metadata + "MethodDefinitionHandle>";
        return ".method int32 AccessorCount() {\n.locals init (" + definition + " row, " + accessors + " accessors, "
            + methods + " others)\nldstr \"/tmp/other-accessors.dll\"\ncall class FileStream File::OpenRead(string)\n"
            + "newobj instance void " + peReader + "::.ctor(class Stream)\ncall " + reader + " "
            + metadata + "PEReaderExtensions::GetMetadataReader(" + peReader + ")\nldc.i4.1\ncall " + handle + " "
            + metadata + "Ecma335.MetadataTokens::" + kind + "DefinitionHandle(int32)\ncallvirt instance " + definition + " "
            + reader + "::Get" + kind + "Definition(" + handle + ")\nstloc.0\nldloca.s 0\ncall instance " + accessors + " "
            + definition + "::GetAccessors()\nstloc.1\nldloca.s 1\ncall instance " + methods + " " + accessors
            + "::get_Others()\nstloc.2\nldloca.s 2\ncall instance int32 " + methods + "::get_Length()\nret\n}";
    }
}
