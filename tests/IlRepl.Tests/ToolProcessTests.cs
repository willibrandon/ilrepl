using System.Diagnostics;
using System.Globalization;

namespace IlRepl.Tests;

/// <summary>
/// Proves concurrent diagnostic draining and cancellation against real child processes.
/// </summary>
[TestClass]
[TestCategory("ExportConformance")]
public sealed class ToolProcessTests
{
    /// <summary>
    /// Supplies cancellation to the bounded child-process checks.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A child can fill stderr before writing stdout without deadlocking the conformance harness.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_DrainsBothPipesConcurrently()
    {
        var result = await ToolProcess.RunAsync(Start("--export-tool-output"), TestContext.CancellationToken);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(new string('o', 128 * 1024), result.StandardOutput);
        Assert.AreEqual(new string('e', 128 * 1024), result.StandardError);
    }

    /// <summary>
    /// Cancelling a confirmed running child waits for its actual termination before returning.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_CancellationReapsRunningChild()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-tool-cancel-").FullName;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var signal = Path.Combine(directory, "started");
        var task = ToolProcess.RunAsync(Start("--export-tool-wait", signal), cancellation.Token);
        try
        {
            while (!File.Exists(signal))
            {
                if (task.IsCompleted) await task;
                await Task.Delay(10, TestContext.CancellationToken);
            }
            var text = await File.ReadAllTextAsync(signal, TestContext.CancellationToken);
            using var process = Process.GetProcessById(int.Parse(text, CultureInfo.InvariantCulture));
            Assert.IsFalse(process.HasExited);
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
            Assert.IsTrue(process.HasExited);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await task; }
            catch (OperationCanceledException) { }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessStartInfo Start(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(ToolProcessTests).Assembly.Location);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
