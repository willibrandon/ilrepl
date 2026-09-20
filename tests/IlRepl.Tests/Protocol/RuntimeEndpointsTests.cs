using System.Diagnostics;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// A killed runtime's pipes and socket do not stay in the temporary directory.
/// </summary>
[TestClass]
public sealed class RuntimeEndpointsTests
{
    /// <summary>
    /// Supplies cancellation for the real child process.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A runtime that is killed leaves its endpoints behind, and removing them by its process identifier takes them all.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Remove_TakesWhatAKilledRuntimeLeft()
    {
        var token = TestContext.CancellationToken;
        var signal = Path.Join(Path.GetTempPath(), "ilrepl-endpoints-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(RuntimeEndpointsTests).Assembly.Location);
        }

        start.ArgumentList.Add("--export-tool-wait");
        start.ArgumentList.Add(signal);
        using var child = Process.Start(start)!;
        try
        {
            // The child writes this file from managed code, so its runtime and the runtime's endpoints exist by then.
            while (!File.Exists(signal))
            {
                Assert.IsFalse(child.HasExited, "The child ended before it reported.");
                await Task.Delay(20, token);
            }

            Assert.IsNotEmpty(Endpoints(child.Id), "A running runtime is expected to have endpoints in the temporary directory.");
            child.Kill();
            await child.WaitForExitAsync(token);
            Assert.IsNotEmpty(Endpoints(child.Id), "A killed runtime cannot remove its endpoints, which is what this cleans up.");

            RuntimeEndpoints.Remove(child.Id);

            Assert.IsEmpty(Endpoints(child.Id));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }

            File.Delete(signal);
        }
    }

    private static string[] Endpoints(int processId) =>
    [
        .. Directory.EnumerateFiles(Path.GetTempPath(), $"dotnet-diagnostic-{processId}-*"),
        .. Directory.EnumerateFiles(Path.GetTempPath(), $"clr-debug-pipe-{processId}-*"),
    ];
}
