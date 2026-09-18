using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Observes genuine partial console output before execution returns while preserving transcript input and replay ordering.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class HostOutputTests
{
    /// <summary>
    /// Supplies cancellation for the real host and filesystem release condition.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The initiating input precedes partial output and appears only once after ordinary execution or explicit replay completes.
    /// </summary>
    /// <param name="replay">Whether to repeat the observation through a fresh session replay runtime.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Streaming_InputPrecedesPartialOutputWithoutDuplicates(bool replay)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var controller = await SessionWorkspaceFixture.StartAsync(token);
        string[] source =
        [
            ".method int32 Work() {", "ldstr \"partial\"", "call void Console::Write(string)",
            "WAIT: ldstr " + LiteralParser.Escape(files.MarkerPath), "call bool File::Exists(string)", "brfalse WAIT",
            "ldstr \" suffix\\n\"", "call void Console::Write(string)", "ldc.i4 42", "ret", "}", "call int32 Work()",
        ];
        foreach (var line in source) Assert.IsTrue((await controller.HandleAsync(line, token)).Succeeded, line);
        var transcript = new Transcript();
        var received = new TaskCompletionSource<TranscriptLine[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.OutputReceived += output =>
        {
            transcript.AppendOutput(output);
            if (output.Text == "partial") received.TrySetResult(transcript.Lines.ToArray());
        };
        try
        {
            await ObserveAsync("ret");
            if (replay)
            {
                File.Delete(files.MarkerPath);
                transcript.Clear();
                received = new TaskCompletionSource<TranscriptLine[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                await ObserveAsync(".session run");
            }
        }
        finally
        {
            await File.WriteAllTextAsync(files.MarkerPath, "release", CancellationToken.None);
        }

        async Task ObserveAsync(string command)
        {
            var pending = controller.HandleAsync(command, token);
            var partial = await received.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.IsFalse(pending.IsCompleted, "The observed partial write precedes invocation completion.");
            var input = Array.FindIndex(partial, line => line.Kind == LineKind.Input
                && line.PlainText.EndsWith("> " + command, StringComparison.Ordinal));
            var output = Array.FindIndex(partial, line => line.Kind == LineKind.Output && line.PlainText == "partial");
            Assert.IsGreaterThanOrEqualTo(0, input);
            Assert.IsGreaterThan(input, output);
            await File.WriteAllTextAsync(files.MarkerPath, "release", token);
            var reply = await pending.WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(line => line.PlainText)));
            foreach (var line in reply.Lines) transcript.Add(line);
            Assert.ContainsSingle(transcript.Lines.Where(line => line.Kind == LineKind.Input
                && line.PlainText.EndsWith("> " + command, StringComparison.Ordinal)));
            var completeOutput = Assert.ContainsSingle(transcript.Lines.Where(line => line.Kind == LineKind.Output));
            Assert.AreEqual("partial suffix", completeOutput.PlainText);
            Assert.ContainsSingle(transcript.Lines.Where(line => line.Kind == LineKind.Result
                && line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal)));
            Assert.DoesNotContain(line => line.Kind == LineKind.Output, reply.Lines);
        }
    }
}
