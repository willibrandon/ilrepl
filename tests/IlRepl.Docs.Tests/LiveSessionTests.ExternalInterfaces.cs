using IlRepl.Tests.Shared;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser copies preserve the interface identities and member access required by external base classes.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Original and revised copies retain external interfaces through worker comparisons and saved assemblies.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="operation">The cast, interface call, or inherited member operation.</param>
    /// <param name="generic">Whether the interface has a private type argument.</param>
    /// <param name="reimplement">Whether the derived type reimplements the contract.</param>
    [TestMethod]
    [DataRow("chromium", "isinst", false, false)]
    [DataRow("webkit", "isinst", false, false)]
    [DataRow("chromium", "isinst", true, false)]
    [DataRow("webkit", "isinst", true, false)]
    [DataRow("chromium", "castclass", true, false)]
    [DataRow("webkit", "castclass", true, false)]
    [DataRow("chromium", "callvirt", true, false)]
    [DataRow("webkit", "callvirt", true, false)]
    [DataRow("chromium", "castclass", true, true)]
    [DataRow("webkit", "castclass", true, true)]
    [DataRow("chromium", "base-isinst", true, false)]
    [DataRow("webkit", "base-isinst", true, false)]
    [DataRow("chromium", "internal", true, false)]
    [DataRow("webkit", "internal", true, false)]
    [DataRow("chromium", "field", true, false)]
    [DataRow("webkit", "field", true, false)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditPreservesExternalInterfaces(string browser, string operation, bool generic, bool reimplement)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var fixture = ExternalInterfaceFixture.Create(operation, generic, reimplement);
        var expected = reimplement ? 43 : 42;
        var image = Convert.ToBase64String(fixture.Image);
        await RunCorpusCellAsync(page, "ldstr \"/tmp/external-source.dll\"\nldstr \"" + image + "\"\n"
            + "call uint8[] Convert::FromBase64String(string)\ncall void File::WriteAllBytes(string, uint8[])\nldc.i4.1\nret", 1);
        await TypeLineAsync(page, ".load /tmp/external-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call int32 Owner::Probe()\nret", expected);
        await TypeLineAsync(page, ".edit int32 Owner::Probe() as Copy");
        await ExpectCompletionAsync(page, ".edit int32 Owner::Probe() as Copy {");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, "call Copy\nret", expected);
        var parent = await ObserveComparisonResultsAsync(page);
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual(expected.ToString(), side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: match");
        await SubmitEditSourceAsync(page, ".edit Copy {\n" + ExternalInterfaceFixture.EditedMethod(operation, generic) + "\n}",
            "edit Copy committed as revision 2");
        await TypeLineAsync(page, ".compare Copy ()");
        foreach (var (index, value) in new[] { (2, expected), (3, expected + 1) })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual(value.ToString(), side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "Copy: different");
        await TypeLineAsync(page, "call Copy");
        await TypeLineAsync(page, ".save /tmp/external-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/external-copy.dll");
        await TypeLineAsync(page, ".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await TypeLineAsync(page, ".load /tmp/external-source.dll");
        await ExpectCompletionAsync(page, "types)");
        await TypeLineAsync(page, ".load /tmp/external-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [external-copy]IlRepl.Cell::Run()\nret", expected + 1);
    }
}
