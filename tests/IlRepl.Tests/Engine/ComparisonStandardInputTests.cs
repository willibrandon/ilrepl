using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real desktop workers expose captured input through their operating-system standard-input pipe.
/// </summary>
[TestClass]
public sealed class ComparisonStandardInputTests
{
    /// <summary>
    /// Supplies cancellation for the actual comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Raw and managed readers consume the same Unicode input and reach EOF after the captured text.
    /// </summary>
    /// <param name="raw">Whether to open the underlying input stream.</param>
    /// <param name="consumePrefix">Whether a raw read consumes the first byte before the text reader starts.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Compare_InputReadersReceiveCapturedText(bool raw, bool consumePrefix)
    {
        const string input = "Aéλ漢字🌍\nsecond line\n";
        var body = consumePrefix ? "call class Stream Console::OpenStandardInput()\n"
            + "callvirt instance int32 Stream::ReadByte()\ncall void Console::Write(int32)\n" : "";
        body += raw ? "call class Stream Console::OpenStandardInput()\nnewobj instance void StreamReader::.ctor(class Stream)\n"
            : "call class TextReader Console::get_In()\n";
        body += "callvirt instance string TextReader::ReadToEnd()\nret";
        var session = IlLines.Load((".method string Read() {\n" + body + "\n}").Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var package = ComparisonCapture.Create(session, "Copy () --stdin " + LiteralParser.Escape(input));

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual(consumePrefix ? input[1..] : input, side.Result!.Value);
            Assert.AreEqual(consumePrefix ? "65" : "", side.StandardOutput);
        }
    }

    /// <summary>
    /// Large input reaches EOF or permits worker cleanup, with a short execution deadline only for the infinite loop.
    /// </summary>
    /// <param name="behavior">Whether the worker reads, ignores input, exits, times out, or is cancelled.</param>
    [TestMethod]
    [DataRow("read")]
    [DataRow("ignore")]
    [DataRow("exit")]
    [DataRow("spin")]
    [DataRow("cancel")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Compare_LargeInputDoesNotBlockWorkerLifecycle(string behavior)
    {
        var body = behavior switch
        {
            "read" => "call class Stream Console::OpenStandardInput()\nnewobj instance void StreamReader::.ctor(class Stream)\n"
                + "callvirt instance string TextReader::ReadToEnd()\ncallvirt instance int32 String::get_Length()\nret",
            "exit" => "ldc.i4.7\ncall void Environment::Exit(int32)\nldc.i4.0\nret",
            "spin" or "cancel" => "AGAIN: br AGAIN",
            _ => "ldc.i4.s 42\nret",
        };

        var session = IlLines.Load((".method int32 Read() {\n" + body + "\n}").Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var input = new string('λ', 1_000_000);
        var request = behavior == "spin" ? "Copy () --timeout 2s" : "Copy ()";
        var package = ComparisonCapture.Create(session, request) with { StandardInput = input };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        if (behavior == "cancel")
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        }

        var result = await ProcessComparisonRunner.RunAsync(package, cancellation.Token);

        Assert.AreEqual(behavior is "exit" or "spin" or "cancel" ? "incomplete" : "match", result.Outcome,
            $"original: {result.Original.Outcome}: {result.Original.Detail}; edited: {result.Edited.Outcome}: {result.Edited.Detail}");
        var outcome = behavior switch { "exit" => "crashed", "spin" => "timeout", "cancel" => "cancelled", _ => "completed" };
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual(outcome, side.Outcome, side.Detail);
            if (behavior is "read" or "ignore")
            {
                Assert.IsNull(side.Exception);
                Assert.AreEqual(behavior == "read" ? "1000000" : "42", side.Result!.Value);
            }
        }
    }
}
