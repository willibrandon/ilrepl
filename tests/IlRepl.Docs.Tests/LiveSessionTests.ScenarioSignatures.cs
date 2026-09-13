namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser scenarios reject incompatible signatures before launching comparison workers.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Signature rejection leaves the edited scenario and original method callable in the parent session.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="returnType">Whether the edit changes its return type instead of its parameter count.</param>
    /// <returns>The completed rejection and recovery assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", false)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ScenarioRejectsIncompatibleSignatures(string browser, bool returnType)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var signature = returnType ? "int64 Read(int32 value)" : "int32 Read(int32 value, int32 other)";
        var body = returnType ? "ldarg.0\nconv.i8\nldc.i8 1\nadd\nret" : "ldarg.1\nret";
        var scenario = returnType ? "ldc.i4.s 41\ncall Copy\nconv.i4\nret" : "ldc.i4.s 41\nldc.i4.s 42\ncall Copy\nret";
        await SubmitEditSourceAsync(page, ".method int32 Read(int32 value) {\nldarg.0\nret\n}\n"
            + ".edit Read as Copy {\n.method public static " + signature + " cil managed {\n" + body + "\n}\n}\n"
            + ".method int32 Scenario() {\n" + scenario + "\n}", "end of method Scenario");

        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "the original and edited signatures must match");
        var text = await BufferTextAsync(page);
        Assert.DoesNotContain("setup-failed", text);
        Assert.DoesNotContain("engine error:", text);
        await InputIdleAsync(page);
        await EmptyPromptAsync(page);
        await RunCorpusCellAsync(page, "call Scenario\nret", 42);
        await RunCorpusCellAsync(page, "ldc.i4.s 41\ncall Read\nret", 41);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }

    /// <summary>
    /// Matching signature modifiers preserve scalar, reference, and void observations in real browser workers.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="kind">The selected method's return shape.</param>
    /// <returns>The completed modified-signature assertions.</returns>
    [TestMethod]
    [DataRow("chromium", "scalar")]
    [DataRow("webkit", "scalar")]
    [DataRow("chromium", "reference")]
    [DataRow("webkit", "reference")]
    [DataRow("chromium", "void")]
    [DataRow("webkit", "void")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ScenarioPreservesSignatureModifiers(string browser, string kind)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        const string modifier = "System.Runtime.CompilerServices.IsConst";
        var result = kind == "reference" ? "int32&" : kind == "void" ? "void" : "int32";
        var parameter = kind == "reference" ? "int32&" : "int32";
        var signature = result + " modopt(" + modifier + ") Read(" + parameter + " modreq(" + modifier + ") value)";
        var body = kind == "void" ? "ldarg.0\ncall void Console::Write(int32)\nret" : "ldarg.0\nret";
        var changed = body.Replace("ldarg.0", kind == "reference"
            ? "ldarg.0\ndup\ndup\nldind.i4\nldc.i4.1\nadd\nstind.i4" : "ldarg.0\nldc.i4.1\nadd", StringComparison.Ordinal);
        var scenario = kind == "reference"
            ? ".locals init (int32 value)\nldc.i4.s 41\nstloc.0\nldloca.s 0\ncall Copy\nldind.i4\nret"
            : "ldc.i4.s 41\ncall Copy\n" + (kind == "void" ? "ldc.i4.s 42\n" : "") + "ret";
        await SubmitEditSourceAsync(page, ".method " + signature + " {\n" + body + "\n}\n"
            + ".edit Read as Copy {\n.method public static " + signature + " cil managed {\n" + changed + "\n}\n}\n"
            + ".method int32 Scenario() {\n" + scenario + "\n}", "end of method Scenario");

        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: different");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.DoesNotContain("InvalidProgramException", text);
        if (kind != "void")
        {
            Assert.Contains("\"41\"", text);
            Assert.Contains("\"42\"", text);
        }

        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
    }
}
