using IlRepl.Tests.Shared;
using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Browser activation creates renamed types through direct calls, worker comparisons, and saved assemblies.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// String overloads preserve copied type identity, constructor arguments, and the caller's unchanged input strings.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="overload">The number of activation parameters.</param>
    /// <param name="nested">Whether to instantiate a nested generic type.</param>
    /// <param name="qualified">Whether to supply the source assembly's full identity.</param>
    [TestMethod]
    [DataRow("chromium", 2, false, false)]
    [DataRow("webkit", 2, false, false)]
    [DataRow("chromium", 2, false, true)]
    [DataRow("webkit", 2, false, true)]
    [DataRow("chromium", 2, true, true)]
    [DataRow("webkit", 2, true, true)]
    [DataRow("chromium", 3, true, false)]
    [DataRow("webkit", 3, true, false)]
    [DataRow("chromium", 8, false, false)]
    [DataRow("webkit", 8, false, false)]
    [DataRow("chromium", 8, true, true)]
    [DataRow("webkit", 8, true, true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_EditTranslatesStringActivation(string browser, int overload, bool nested, bool qualified)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await Submit(ActivationExamples.Source(overload, nested), "end of class Activation.Owner");
        var assembly = qualified ? "ilrepl.types.1, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" : null;
        var name = ActivationExamples.Name(nested, overload == 8);
        var assemblyLiteral = assembly is null ? "null" : Quote(assembly);
        var arguments = "(" + assemblyLiteral + ", " + Quote(name) + ")";
        var inputs = (assembly is null ? "ldnull" : "ldstr " + Quote(assembly)) + "\nldstr " + Quote(name) + "\n";
        await RunCorpusCellAsync(page, inputs + "call int32 Activation.Owner::Read(string, string)\nret", 42);
        await Submit(".edit int32 Activation.Owner::Read(string, string) as Copy {\n"
            + ActivationExamples.Method(overload, nested, false) + "\n}", "edit Copy committed as revision 1");
        await RunCorpusCellAsync(page, inputs + "call Copy\nret", 42);
        var parent = await ObserveComparisonResultsAsync(page);
        await Command(".compare Copy " + arguments);
        foreach (var index in new[] { 0, 1 })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual("42", side.GetProperty("result").GetProperty("value").GetString());
            Assert.AreEqual("42\n" + assembly + "\n" + name + "\n", side.GetProperty("standardOutput").GetString());
        }

        await ExpectComparisonTextAsync(page, "edited: completed");
        await Submit(".edit Copy {\n" + ActivationExamples.Method(overload, nested, true) + "\n}",
            "edit Copy committed as revision 2");
        await Command(".compare Copy " + arguments);
        foreach (var (index, expected) in new[] { (2, "42"), (3, "43") })
        {
            var side = await WaitForComparisonResultAsync(parent, index);
            Assert.AreEqual("completed", side.GetProperty("outcome").GetString(), side.GetRawText());
            Assert.AreEqual(expected, side.GetProperty("result").GetProperty("value").GetString());
        }

        await ExpectComparisonTextAsync(page, "edited: completed");
        foreach (var line in (inputs + "call Copy").Split('\n')) await Command(line);
        await Command(".save /tmp/activation-copy.dll");
        await ExpectCompletionAsync(page, "wrote /tmp/activation-copy.dll");
        await Command(".reset");
        await ExpectCompletionAsync(page, "cell, declarations, methods, and types cleared");
        await Command(".load /tmp/activation-copy.dll");
        await ExpectCompletionAsync(page, "types)");
        await RunCorpusCellAsync(page, "call object [activation-copy]IlRepl.Cell::Run()\nret", 43);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));

        async Task Submit(string source, string expected)
        {
            await Command(source);
            try
            {
                await ExpectCompletionAsync(page, expected);
            }
            catch (PlaywrightException)
            {
                TestContext.WriteLine(await page.EvaluateAsync<string>(
                    "() => JSON.stringify({ restart: window.ilreplLastRestart, sessions: window.ilreplSessionCount })"));
                throw;
            }
        }

        async Task Command(string source)
        {
            await InputIdleAsync(page);
            await PasteAsync(page, source);
            await SendTerminalInputAsync(page, "\r");
        }

        static string Quote(string value) => "\"" + value + "\"";
    }
}
