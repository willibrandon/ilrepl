using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Process comparisons observe managed and direct output in the order written to each actual stream.
/// </summary>
[TestClass]
public sealed class ComparisonOutputOrderTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Mixed writes preserve order and split UTF-8 sequences on successful execution and process exit.
    /// </summary>
    /// <param name="error">Whether the writes use standard error.</param>
    /// <param name="reverse">Whether the edit reverses the managed and direct writes.</param>
    /// <param name="crash">Whether each method exits its worker after writing.</param>
    /// <returns>The completed stream, outcome, and parent recovery assertions.</returns>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    public async Task Run_MixedOutput_PreservesActualStreamOrder(bool error, bool reverse, bool crash)
    {
        string Source(bool reversed) => ComparisonOutputExamples.Source(error, reversed).Replace("ldc.i4.s 42",
            crash ? "ldc.i4.s 17\ncall void Environment::Exit(int32)\nldc.i4.s 42" : "ldc.i4.s 42", StringComparison.Ordinal);
        var session = IlLines.Load(Source(false).Split('\n'));
        var edit = session.PrepareEdit("Work", "Copy");
        session.CommitEdit(edit.Name, Source(reverse));

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"), TestContext.CancellationToken);

        Assert.AreEqual(crash ? "incomplete" : reverse ? "different" : "match", result.Outcome,
            result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("\u00e9B", error ? result.Original.StandardError : result.Original.StandardOutput);
        Assert.AreEqual(reverse ? "B\u00e9" : "\u00e9B", error ? result.Edited.StandardError : result.Edited.StandardOutput);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual(crash ? "crashed" : "completed", side.Outcome, side.Detail);
            Assert.AreEqual("", error ? side.StandardOutput : side.StandardError);
            if (!crash)
            {
                Assert.AreEqual("42", side.Result!.Value);
                Assert.HasCount(1, side.Invocations);
            }
        }

        session.AddLine("ldc.i4.s 42");
        Assert.AreEqual(42, session.Run().Value);
    }

    /// <summary>
    /// The stream limit covers the combined managed and direct output without counting managed writes twice.
    /// </summary>
    /// <param name="length">The number of characters written by each source.</param>
    /// <param name="outcome">The expected side outcome under a shared stream limit.</param>
    /// <returns>The completed bounded stream assertions.</returns>
    [TestMethod]
    [DataRow(128, "completed")]
    [DataRow(129, "output-limit")]
    public async Task Run_MixedOutputLimit_CountsEachWriteOnce(int length, string outcome)
    {
        var session = IlLines.Load((".method int32 Work() {\nldstr \"" + new string('A', length)
            + "\"\ncall void Console::Write(string)\ncall class System.IO.Stream Console::OpenStandardOutput()\n"
            + "call class System.Text.Encoding System.Text.Encoding::get_UTF8()\nldstr \"" + new string('B', length)
            + "\"\ncallvirt instance uint8[] System.Text.Encoding::GetBytes(string)\nldc.i4.0\nldc.i4 " + length
            + "\ncallvirt instance void System.IO.Stream::Write(uint8[], int32, int32)\nldc.i4.s 42\nret\n}").Split('\n'));
        var edit = session.PrepareEdit("Work", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var package = ComparisonCapture.Create(session, "Copy ()") with { OutputLimit = 256 };

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual(outcome == "completed" ? "match" : "incomplete", result.Outcome);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual(outcome, side.Outcome, side.Detail);
            if (outcome == "completed")
            {
                Assert.AreEqual(new string('A', length) + new string('B', length), side.StandardOutput);
            }
        }
    }
}
