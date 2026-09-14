using System.Text.Json;
using IlRepl.Tests.Shared;
using Microsoft.Playwright;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Abnormal browser executions retain their captured output through the real worker and supervisor protocol.
/// </summary>
public sealed partial class LiveSessionTests
{
    /// <summary>
    /// Terminating a comparison keeps both stream prefixes, their write order, and the parent session.
    /// </summary>
    /// <param name="browser">The browser engine.</param>
    /// <param name="failure">The actual exit, abort, timeout, cancellation, or output overflow.</param>
    /// <returns>The completed worker result and parent recovery assertions.</returns>
    [TestMethod]
    [DataRow("chromium", "exit")]
    [DataRow("webkit", "exit")]
    [DataRow("chromium", "abort")]
    [DataRow("webkit", "abort")]
    [DataRow("chromium", "timeout")]
    [DataRow("webkit", "timeout")]
    [DataRow("chromium", "stdout-limit")]
    [DataRow("webkit", "stdout-limit")]
    [DataRow("chromium", "stderr-limit")]
    [DataRow("webkit", "stderr-limit")]
    [DataRow("chromium", "cancelled")]
    [DataRow("webkit", "cancelled")]
    [Timeout(240_000, CooperativeCancellation = true)]
    public async Task LiveSession_ComparisonFailureRetainsOutput(string browser, string failure)
    {
        await using var context = await NewContextAsync(GetBrowser(browser));
        var page = await OpenSessionAsync(context);
        var editedWorker = new TaskCompletionSource<IWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        page.Worker += (_, worker) =>
        {
            if (worker.Url.EndsWith("/comparison-worker.js", StringComparison.Ordinal) && Interlocked.Increment(ref created) == 2)
            {
                editedWorker.TrySetResult(worker);
            }
        };
        var parent = await ObserveComparisonResultsAsync(page);
        var action = failure switch
        {
            "exit" => "ldc.i4.7\ncall void Environment::Exit(int32)\n",
            "abort" => "ldstr \"comparison abort\"\ncall void Environment::FailFast(string)\n",
            "timeout" => "LOOP: br LOOP\n",
            "cancelled" => "ldstr \"/work/output-ready\"\nldstr \"ready\"\ncall void System.IO.File::WriteAllText(string, string)\n"
                + "ldc.i4.m1\ncall class System.Threading.Tasks.Task System.Threading.Tasks.Task::Delay(int32)\nret\n",
            "stdout-limit" => "LOOP: ldstr \"overflow\"\ncall void Console::Write(string)\nbr LOOP\n",
            _ => "LOOP: call class System.IO.TextWriter Console::get_Error()\nldstr \"overflow\"\n"
                + "callvirt instance void System.IO.TextWriter::Write(string)\nbr LOOP\n",
        };
        var taskReturn = "call class System.Threading.Tasks.Task System.Threading.Tasks.Task::get_CompletedTask()\nret";
        var source = ComparisonOutputExamples.Source(false, false)
            .Replace("int32 Work", "class System.Threading.Tasks.Task Work", StringComparison.Ordinal)
            .Replace("ldc.i4.s 42\nret", "call class System.IO.TextWriter Console::get_Error()\nldstr \"diagnostic\"\n"
                + "callvirt instance void System.IO.TextWriter::Write(string)\n"
                + "call class System.IO.Stream Console::OpenStandardOutput()\nldc.i4 195\n"
                + "callvirt instance void System.IO.Stream::WriteByte(uint8)\n" + action + taskReturn, StringComparison.Ordinal);
        await SubmitEditSourceAsync(page, ".method class System.Threading.Tasks.Task Work() {\n" + taskReturn
            + "\n}\n.edit Work as Failure {\n" + source + "\n}", "edit Failure committed as revision 1");
        await TypeLineAsync(page, ".compare Failure ()" + (failure == "timeout" ? " --timeout 1s" : ""));
        if (failure == "cancelled")
        {
            var child = await editedWorker.Task.WaitAsync(TestContext.CancellationToken);
            await child.EvaluateAsync<object?>("""
                async () => {
                  while (!globalThis.getDotnetRuntime?.(0)?.Module.FS.analyzePath('/work/output-ready').exists)
                    await new Promise(resolve => setTimeout(resolve, 10));
                }
                """).WaitAsync(TestContext.CancellationToken);
            await page.Keyboard.PressAsync("Control+c");
        }

        var outcome = failure is "exit" or "abort" ? "crashed" : failure.EndsWith("-limit", StringComparison.Ordinal)
            ? "output-limit" : failure;
        var result = await WaitForComparisonResultAsync(parent, 1);
        Assert.AreEqual(outcome, result.GetProperty("outcome").GetString());
        var stdout = result.GetProperty("standardOutput").GetString()!;
        var stderr = result.GetProperty("standardError").GetString()!;
        Assert.StartsWith("\u00e9B\ufffd", stdout);
        Assert.StartsWith("diagnostic", stderr);
        if (failure.EndsWith("-limit", StringComparison.Ordinal))
        {
            Assert.AreEqual(65536, failure == "stdout-limit" ? stdout.Length : stderr.Length);
        }

        Assert.AreEqual(1, await page.EvaluateAsync<int>("() => window.ilreplSessionCount"));
        await InputIdleAsync(page);
        await EmptyPromptAsync(page);
        await RunCorpusCellAsync(page, "call Work\npop\nldc.i4.s 42\nret", 42);
    }

    private static async Task<IWorker> ObserveComparisonResultsAsync(IPage page)
    {
        var parent = page.Workers.Single(worker => worker.Url.EndsWith("/worker.js", StringComparison.Ordinal));
        await parent.EvaluateAsync<object?>("""
            () => {
              self.comparisonResults = [];
              self.addEventListener('message', ({ data }) => {
                if (data.type === 'comparison-result') self.comparisonResults.push(JSON.parse(data.result));
              });
            }
            """);
        return parent;
    }

    private Task<JsonElement> WaitForComparisonResultAsync(IWorker parent, int index) => parent.EvaluateAsync<JsonElement>("""
        async index => {
          while (self.comparisonResults.length <= index) await new Promise(resolve => setTimeout(resolve, 10));
          return self.comparisonResults[index];
        }
        """, index).WaitAsync(TestContext.CancellationToken);
}
