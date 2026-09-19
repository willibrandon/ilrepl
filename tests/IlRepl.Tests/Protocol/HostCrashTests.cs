using System.Text;
using IlRepl.Engine;
using IlRepl.Processes;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Exercises actual runtime crash and allocation-failure paths in disposable execution processes.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class HostCrashTests
{
    /// <summary>
    /// Supplies cancellation for real host processes and diagnostic observations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Real runtime failures publish typed exit diagnostics before a failed invocation reply.
    /// </summary>
    /// <param name="failure">The process-fatal user IL to execute.</param>
    [TestMethod]
    [DataRow("stack-overflow")]
    [DataRow("fail-fast")]
    [DataRow("fail-fast-large")]
    [DataRow("access-violation")]
    [DataRow("terminate-large")]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task FatalUserCode_ReportsObservedExitAndDiagnosticTail(string failure)
    {
        var token = TestContext.CancellationToken;
        await using var engine = await HostProcessEngine.StartAsync(HostPaths.HostAssembly, RepoPaths.Root,
            new Dictionary<string, string?> { ["DOTNET_DbgEnableMiniDump"] = "0", ["COMPlus_DbgEnableMiniDump"] = "0" }, token);
        var exited = new TaskCompletionSource<HostExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Exited += value => exited.TrySetResult(value);
        string[] source = failure switch
        {
            "stack-overflow" => [".method int32 Recurse(int32 n) {", "ldarg.0", "ldc.i4.1", "add",
                "call int32 Recurse(int32)", "ldarg.0", "add", "ret", "}", "ldc.i4.0", "call int32 Recurse(int32)"],
            "fail-fast" => ["ldstr \"ilrepl-fatal-diagnostic\"", "call void Environment::FailFast(string)"],
            "fail-fast-large" => [".locals init (uint8[] bytes)",
                "call class System.Text.Encoding System.Text.Encoding::get_UTF8()",
                "ldstr " + LiteralParser.Escape(new string('あ', 30_000) + " ilrepl-fatal-tail-marker"),
                "callvirt instance uint8[] System.Text.Encoding::GetBytes(string)", "stloc.0",
                "call class System.IO.Stream Console::OpenStandardError()", "ldloc.0", "ldc.i4.0", "ldloc.0", "ldlen", "conv.i4",
                "callvirt instance void System.IO.Stream::Write(uint8[], int32, int32)",
                "ldstr \"ilrepl-fatal-diagnostic\"", "call void Environment::FailFast(string)"],
            // Terminating as the write returns leaves the last pipe buffer unread when the exit is signalled, as a stack overflow does.
            "terminate-large" => [".locals init (uint8[] bytes)",
                "call class System.Text.Encoding System.Text.Encoding::get_UTF8()",
                "ldstr " + LiteralParser.Escape(new string('x', 60_000) + " ilrepl-terminate-tail-marker"),
                "callvirt instance uint8[] System.Text.Encoding::GetBytes(string)", "stloc.0",
                "call class System.IO.Stream Console::OpenStandardError()", "ldloc.0", "ldc.i4.0", "ldloc.0", "ldlen", "conv.i4",
                "callvirt instance void System.IO.Stream::Write(uint8[], int32, int32)",
                "call class System.Diagnostics.Process System.Diagnostics.Process::GetCurrentProcess()",
                "callvirt instance void System.Diagnostics.Process::Kill()"],
            _ => ["ldc.i8 0x123456781000", "conv.u", "ldind.i4"],
        };
        foreach (var line in source)
        {
            var reply = await engine.HandleAsync(line, token);
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(item => item.PlainText)));
        }
        await Assert.ThrowsAsync<HostProtocolException>(() => engine.HandleAsync("ret", token));
        Assert.IsTrue(exited.Task.IsCompletedSuccessfully, "Exit publication must precede the failed execution reply.");
        var observed = await exited.Task;
        Assert.AreEqual(engine.ProcessId, observed.ProcessId);
        Assert.IsFalse(observed.Expected);
        Assert.IsNotNull(observed.ExitCode);
        Assert.AreNotEqual(0, observed.ExitCode.Value);
        Assert.IsLessThanOrEqualTo(65536, Encoding.UTF8.GetByteCount(observed.StandardError));
        if (failure == "fail-fast") Assert.Contains("ilrepl-fatal-diagnostic", observed.StandardError);
        if (failure == "fail-fast-large")
        {
            Assert.Contains("ilrepl-fatal-tail-marker", observed.StandardError);
            Assert.IsGreaterThan(32_768, Encoding.UTF8.GetByteCount(observed.StandardError));
        }
        if (failure == "terminate-large") Assert.EndsWith("ilrepl-terminate-tail-marker", observed.StandardError);
        if (failure == "stack-overflow") Assert.Contains("Stack overflow", observed.StandardError);
        if (failure == "access-violation") Assert.Contains("AccessViolation", observed.StandardError);
    }

    /// <summary>
    /// An allocation rejected by the real GC remains an ordinary cell failure and preserves the runtime.
    /// </summary>
    [TestMethod]
    public async Task AllocationFailure_RemainsRecoverableWithTheSameRuntime()
    {
        var token = TestContext.CancellationToken;
        await using var engine = await HostProcessEngine.StartAsync(HostPaths.HostAssembly, RepoPaths.Root,
            new Dictionary<string, string?> { ["DOTNET_GCHeapHardLimit"] = "8000000" }, token);
        var process = engine.ProcessId;
        Assert.IsTrue((await engine.HandleAsync("ldc.i4 268435456", token)).Succeeded);
        Assert.IsTrue((await engine.HandleAsync("newarr uint8", token)).Succeeded);
        var failed = await engine.HandleAsync("ret", token);
        Assert.IsFalse(failed.Succeeded);
        Assert.Contains("OutOfMemoryException", string.Join('\n', failed.Lines.Select(line => line.PlainText)));
        Assert.AreEqual(process, engine.ProcessId);
        Assert.IsTrue((await engine.HandleAsync("ldc.i4 42", token)).Succeeded);
        var recovered = await engine.HandleAsync("ret", token);
        Assert.IsTrue(recovered.Succeeded);
        Assert.Contains("= 42 : int32", string.Join('\n', recovered.Lines.Select(line => line.PlainText)));
    }
}
