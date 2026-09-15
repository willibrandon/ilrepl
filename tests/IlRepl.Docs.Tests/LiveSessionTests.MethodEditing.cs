using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Exercises method editing and isolated execution through the browser terminal and real worker runtimes.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Opening a disassembly fills the editor, keyboard edits commit, and history retains the complete edited definition.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed interactive editor assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_MethodEditOpensAndEditsCompleteDocument(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, ".method int32 Answer() {\nldc.i4.s 42\nret\n}", "end of method Answer");
        await TypeLineAsync(page, ".dis Answer");
        await ExpectCompletionAsync(page, "ldc.i4.s 42");
        await TypeLineAsync(page, ".edit");
        await ExpectCompletionAsync(page, "Enter sends 8 lines");
        Assert.Contains(".edit Answer as Answer_Edit {", await BufferTextAsync(page));
        await page.Keyboard.PressAsync("Control+Home");
        for (var index = 0; index < 4; index++)
        {
            await page.Keyboard.PressAsync("ArrowDown");
        }

        await page.Keyboard.PressAsync("End");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync("3");
        await page.Keyboard.PressAsync("Enter");
        await ExpectCompletionAsync(page, "edit Answer_Edit committed as revision 1");
        await RunCorpusCellAsync(page, "call Answer_Edit\nret", 43);
        var history = await StoredHistoryAsync(page);
        Assert.Contains(entry => entry.StartsWith(".edit Answer as Answer_Edit {", StringComparison.Ordinal)
            && entry.Contains("ldc.i4.s 43", StringComparison.Ordinal) && entry.TrimEnd().EndsWith('}'), history);
        await RunCorpusCellAsync(page, "call Answer\nret", 42);
    }

    /// <summary>
    /// Ctrl+C terminates comparison workers and preserves the same parent runtime and its callable declarations.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed cancellation assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_MethodComparisonCancellationPreservesParent(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .method int32 Answer() {
              ldc.i4.s 42
              ret
            }
            .edit Answer as Hanging {
              .method public static int32 Answer() cil managed {
                LOOP: br LOOP
              }
            }
            """, "edit Hanging committed as revision 1");
        await TypeLineAsync(page, ".compare Hanging ()");
        await ExpectCompletionAsync(page, "Ctrl+C cancels");
        await page.Keyboard.PressAsync("Control+c");
        await ExpectComparisonTextAsync(page, "cancelled");
        Assert.Contains("Hanging: incomplete", await BufferTextAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        await RunCorpusCellAsync(page, "call Answer\nret", 42);
    }

    /// <summary>
    /// A scripted edit commits, displays its diff, compares in separate workers, and leaves the parent callable.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed browser assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_MethodEditComparesInFreshWorkers(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .method int32 Identity(int32 value) {
              ldarg.0
              ret
            }
            .edit Identity as Incremented {
              .method public static int32 Identity(int32 value) cil managed {
                ldarg.0
                ldc.i4.1
                add
                ret
              }
            }
            """, "edit Incremented committed as revision 1");
        await TypeLineAsync(page, ".diff Incremented");
        await ExpectCompletionAsync(page, "+ ldc.i4.1");
        await TypeLineAsync(page, ".compare Incremented (41)");
        await ExpectComparisonTextAsync(page, "Incremented: different");
        var text = await BufferTextAsync(page);
        Assert.Contains("original: completed", text);
        Assert.Contains("edited: completed", text);
        Assert.Contains("\"41\"", text);
        Assert.Contains("\"42\"", text);
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        await RunCorpusCellAsync(page, "ldc.i4.s 41\ncall Incremented\nret", 42);
        Assert.DoesNotContain("error:", await BufferTextAsync(page));
    }

    /// <summary>
    /// A selected method that hangs is terminated without restarting or losing the parent session.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <returns>The completed browser assertions.</returns>
    [TestMethod]
    [DataRow("chromium")]
    [DataRow("webkit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_MethodComparisonTimeoutPreservesParent(string browser)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        await SubmitEditSourceAsync(page, """
            .method int32 Answer() {
              ldc.i4.s 42
              ret
            }
            .edit Answer as Hanging {
              .method public static int32 Answer() cil managed {
                LOOP: br LOOP
              }
            }
            """, "edit Hanging committed as revision 1");
        await TypeLineAsync(page, ".compare Hanging () --timeout 100ms");
        await ExpectComparisonTextAsync(page, "edited: timeout");
        Assert.Contains("Hanging: incomplete", await BufferTextAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        await RunCorpusCellAsync(page, "call Answer\nret", 42);
    }

    /// <summary>
    /// Real process exits and unbounded output terminate comparison workers while the parent remains callable.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="failure">The failure generated by the edited method.</param>
    /// <returns>The completed worker failure and parent recovery assertions.</returns>
    [TestMethod]
    [DataRow("chromium", "crashed")]
    [DataRow("webkit", "crashed")]
    [DataRow("chromium", "output-limit")]
    [DataRow("webkit", "output-limit")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_MethodComparisonFailurePreservesParent(string browser, string failure)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var parent = await ObserveComparisonResultsAsync(page);
        var body = failure == "crashed"
            ? "ldc.i4.7\ncall void Environment::Exit(int32)\nldc.i4.0\nret"
            : "LOOP: ldstr \"unbounded output\"\ncall void Console::Write(string)\nbr LOOP";
        await SubmitEditSourceAsync(page, """
            .method int32 Answer() {
              ldc.i4.s 42
              ret
            }
            .edit Answer as Failure {
              .method public static int32 Answer() cil managed {
            """ + "\n" + body + "\n}\n}", "edit Failure committed as revision 1");
        await TypeLineAsync(page, ".compare Failure ()");
        var result = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual(failure, result.GetProperty("outcome").GetString());
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        await InputIdleAsync(page);
        await EmptyPromptAsync(page);
        await RunCorpusCellAsync(page, "call Answer\nret", 42);
    }

    /// <summary>
    /// Copied structs from separate session declarations preserve boxed reference identity in real browser comparisons.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="split">Whether the edited method creates independent equal boxes.</param>
    /// <returns>The completed identity, dependency-report, inspection, and parent-runtime assertions.</returns>
    [TestMethod]
    [DataRow("chromium", false)]
    [DataRow("webkit", false)]
    [DataRow("chromium", true)]
    [DataRow("webkit", true)]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_MethodComparisonPreservesBoxedStructIdentity(string browser, bool split)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        const string body = """
            .locals init (object[] values, object boxed, valuetype Payload value)
            ldloca.s 2
            ldc.i4.s 42
            stfld int32 Payload::Number
            ldloc.2
            box Payload
            stloc.1
            ldc.i4.2
            newarr object
            stloc.0
            ldloc.0
            ldc.i4.0
            ldloc.1
            stelem.ref
            ldloc.0
            ldc.i4.1
            ldloc.1
            stelem.ref
            ldloc.0
            ret
            """;
        var edited = split ? body.Replace("ldloc.1", "ldloc.2\nbox Payload", StringComparison.Ordinal) : body;
        var source = """
            .class public sequential sealed Payload extends System.ValueType {
              .field public int32 Number
            }
            """ + "\n.method object[] Pair() {\n" + body + "\n}\n.edit Pair as Copy {\n"
            + ".method public static object[] Pair() cil managed {\n" + edited + "\n}\n}\n"
            + ".method int32 Scenario() {\ncall Copy\nldlen\nconv.i4\nret\n}";
        await SubmitEditSourceAsync(page, source, "end of method Scenario");
        await InputIdleAsync(page);
        await TypeLineAsync(page, "br DONE");
        await InputIdleAsync(page);
        await TypeLineAsync(page, ".il");
        await ExpectCompletionAsync(page, "object Run()");
        Assert.Contains("br DONE", await BufferTextAsync(page));
        await InputIdleAsync(page);
        await TypeLineAsync(page, ".clear");
        await ExpectCompletionAsync(page, "cell cleared (declarations kept)");
        await InputIdleAsync(page);
        await TypeLineAsync(page, ".methods Copy");
        await ExpectCompletionAsync(page, "copied: public");
        Assert.Contains("Payload::Number", await BufferTextAsync(page));
        await InputIdleAsync(page);
        await TypeLineAsync(page, ".compare Copy using Scenario");
        await ExpectComparisonTextAsync(page, "Copy: " + (split ? "different" : "match"));
        Assert.Contains("reference #2", await BufferTextAsync(page));
        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        await RunCorpusCellAsync(page, "ldc.i4.s 42\nret", 42);
    }

    private static async Task SubmitEditSourceAsync(IPage page, string source, string expected, float timeout = 30_000)
    {
        await PasteAsync(page, source);
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#terminal")).ToContainTextAsync(expected, new() { Timeout = timeout });
    }

    private static async Task ExpectComparisonTextAsync(IPage page, string expected)
    {
        try
        {
            await page.WaitForFunctionAsync("""
                expected => {
                  const buffer = window.ilreplTerminal.buffer.active;
                  return Array.from({ length: buffer.length }, (_, row) =>
                    buffer.getLine(row)?.translateToString(true) ?? '').some(line =>
                      line.includes(expected) || line.includes('edited: setup-failed') || line.includes('engine error:'));
                }
                """, expected, new() { Timeout = 120_000 });
            Assert.Contains(expected, await BufferTextAsync(page));
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException("comparison did not complete: " + await BrowserWaitStateAsync(page), exception);
        }
    }
}
