using System.Text.Json;
using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Real browser satellites preserve original resolution while copied satellite lookups reject recoverably.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Both overloads and dispatch forms execute real originals, preserve drafts, and compare corrected copies in fresh workers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="versioned">Whether the lookup supplies an explicit version.</param>
    /// <param name="dispatch">The direct, reflection, delegate, token, or lookalike form.</param>
    [TestMethod]
    [DataRow("chromium", false, "direct")]
    [DataRow("webkit", false, "direct")]
    [DataRow("chromium", true, "direct")]
    [DataRow("webkit", true, "direct")]
    [DataRow("chromium", false, "reflection")]
    [DataRow("webkit", false, "reflection")]
    [DataRow("chromium", true, "reflection")]
    [DataRow("webkit", true, "reflection")]
    [DataRow("chromium", false, "delegate")]
    [DataRow("webkit", false, "delegate")]
    [DataRow("chromium", true, "delegate")]
    [DataRow("webkit", true, "delegate")]
    [DataRow("chromium", false, "token")]
    [DataRow("webkit", false, "token")]
    [DataRow("chromium", true, "token")]
    [DataRow("webkit", true, "token")]
    [DataRow("chromium", false, "lookalike")]
    [DataRow("webkit", false, "lookalike")]
    [DataRow("chromium", true, "lookalike")]
    [DataRow("webkit", true, "lookalike")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonPreservesOriginalSatelliteContext(string browser, bool versioned, string dispatch)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var fixture = SatelliteAssemblyFixture.Create(versioned, dispatch);
        var sourcePath = "/tmp/" + fixture.Name + ".dll";
        var satellitePath = "/tmp/" + SatelliteAssemblyFixture.Culture + "/" + fixture.Name + ".resources.dll";
        var directory = "ldstr \"/tmp/" + SatelliteAssemblyFixture.Culture + "\"\n"
            + "call class DirectoryInfo Directory::CreateDirectory(string)\npop\n";
        await RunCorpusCellAsync(page, directory + "ldstr \"" + sourcePath + "\"\nldstr \""
            + Convert.ToBase64String(fixture.Source) + "\"\ncall uint8[] Convert::FromBase64String(string)\n"
            + "call void File::WriteAllBytes(string, uint8[])\nldstr \"" + satellitePath + "\"\nldstr \""
            + Convert.ToBase64String(fixture.Satellite) + "\"\ncall uint8[] Convert::FromBase64String(string)\n"
            + "call void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        foreach (var path in new[] { sourcePath, satellitePath })
        {
            await TypeLineAsync(page, ".load " + path);
            await ExpectCompletionAsync(page, "types)");
        }
        var identity = fixture.Name + ".resources, Version=" + SatelliteAssemblyFixture.Version
            + ", Culture=" + SatelliteAssemblyFixture.Culture + ", PublicKeyToken=null";
        var loadSatellite = "ldstr \"" + identity + "\"\ncall class Assembly Assembly::Load(string)\n";
        await RunCorpusCellAsync(page, loadSatellite + "callvirt instance class AssemblyName Assembly::GetName()\n"
            + "callvirt instance string AssemblyName::get_CultureName()\nldstr \"" + SatelliteAssemblyFixture.Culture
            + "\"\ncall bool String::op_Equality(string, string)\nconv.i4\nldc.i4.2\nmul\nret", 2);
        await RunCorpusCellAsync(page, loadSatellite + "callvirt instance class AssemblyName Assembly::GetName()\n"
            + "callvirt instance class Version AssemblyName::get_Version()\ncallvirt instance string Version::ToString()\n"
            + "ldstr \"" + SatelliteAssemblyFixture.Version
            + "\"\ncall bool String::op_Equality(string, string)\nconv.i4\nldc.i4.3\nmul\nret", 3);
        await RunCorpusCellAsync(page, loadSatellite + "ldstr \"Satellite.payload\"\n"
            + "callvirt instance class Stream Assembly::GetManifestResourceStream(string)\n"
            + "callvirt instance int32 Stream::ReadByte()\nret", 42);
        await RunCorpusCellAsync(page, "call int32 SatelliteInspection.Owner::Read()\nldc.i4.s 100\nadd\nret", 142);
        await TypeLineAsync(page, ".edit int32 SatelliteInspection.Owner::Read() as Copy");
        await ExpectCompletionAsync(page, "Enter sends");
        await page.Keyboard.PressAsync("Enter");
        var supported = dispatch is "token" or "lookalike";
        if (supported)
        {
            await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        }
        else
        {
            await ExpectComparisonTextAsync(page, "reproduce");
            var diagnostic = await BufferTextAsync(page);
            var text = string.Join(" ", diagnostic.Replace('│', ' ').Replace('▉', ' ')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains(SatelliteAssemblyFixture.Problem, text);
            Assert.Contains("GetSatelliteAssembly", text);
            await PromptContainsAsync(page, "}");
            await ClearPromptAsync(page);
            await SubmitEditSourceAsync(page, ".edit Copy {\n.method public static int32 Read() {\nldc.i4.s 43\nret\n}\n}",
                "edit Copy committed as revision 1");
        }
        await RunCorpusCellAsync(page, "call Copy\nldc.i4 200\nadd\nret", supported ? 242 : 243);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        AssertSatelliteSide(await WaitForComparisonResultAsync(parent, 0), "42");
        AssertSatelliteSide(await WaitForComparisonResultAsync(parent, 1), supported ? "42" : "43");
        await InputIdleAsync(page);
        Assert.Contains(supported ? "Copy: match" : "Copy: different", await ReadComparisonTranscriptAsync(page));
        await page.Mouse.WheelAsync(0, 10_000);
        await InputIdleAsync(page);
        await RunCorpusCellAsync(page, "call int32 SatelliteInspection.Owner::Read()\nldc.i4 300\nadd\nret", 342);
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/satellite-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/satellite-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/satellite-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page,
            "call object [satellite-copy]IlRepl.Cell::Run()\nunbox.any int32\nldc.i4 400\nadd\nret", supported ? 442 : 443);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    private static void AssertSatelliteSide(JsonElement side, string expected)
    {
        var json = side.GetRawText();
        Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), json);
        Assert.IsFalse(side.TryGetProperty("exception", out var exception) && exception.ValueKind != JsonValueKind.Null, json);
        Assert.IsTrue(side.TryGetProperty("result", out var result), json);
        Assert.AreEqual("scalar", result.GetProperty("kind").GetString(), json);
        Assert.AreEqual("[System.Private.CoreLib]System.Int32", result.GetProperty("type").GetString(), json);
        Assert.AreEqual(expected, result.GetProperty("value").GetString(), json);
        var invocations = side.GetProperty("invocations");
        Assert.AreEqual(1, invocations.GetArrayLength(), json);
        var invocation = invocations[0];
        Assert.IsFalse(invocation.TryGetProperty("exception", out exception) && exception.ValueKind != JsonValueKind.Null, json);
        var returned = invocation.GetProperty("outputs").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "return").GetProperty("value");
        Assert.AreEqual("scalar", returned.GetProperty("kind").GetString(), json);
        Assert.AreEqual("[System.Private.CoreLib]System.Int32", returned.GetProperty("type").GetString(), json);
        Assert.AreEqual(expected, returned.GetProperty("value").GetString(), json);
    }
}
