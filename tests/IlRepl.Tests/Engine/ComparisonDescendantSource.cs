using System.Diagnostics;
using System.Globalization;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Compared code starts real background descendants and checks whether an earlier side left a process running.
/// </summary>
public static class ComparisonDescendantSource
{
    /// <summary>
    /// Starts a descendant that inherits the worker's pipes, then returns, exits, throws, or waits for termination.
    /// </summary>
    /// <param name="executable">The test executable that provides the child process entry.</param>
    /// <param name="records">The test-owned directory containing process and readiness records.</param>
    /// <param name="grandchild">Whether an intermediate child exits before the comparison worker.</param>
    /// <param name="mode">The way the compared method ends.</param>
    /// <returns>The normal result, or a distinct value if the previous side left a live descendant.</returns>
    public static int Run(string executable, string records, bool grandchild, string mode)
    {
        var record = Path.Combine(records, "processes");
        if (File.Exists(record) && File.ReadAllLines(record).Any(line => IsRunning(int.Parse(line, CultureInfo.InvariantCulture))))
        {
            return -1;
        }

        var ready = Path.Combine(records, Guid.NewGuid().ToString("N"));
        using var process = Start(executable, record, ready, grandchild);
        var wait = Stopwatch.StartNew();
        while (!File.Exists(ready))
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("the descendant did not become ready");
            Thread.Sleep(10);
        }

        if (grandchild) process.WaitForExit();
        if (mode == "exit") Environment.Exit(23);
        if (mode == "throw") throw new InvalidOperationException("worker failure");
        if (mode is "timeout" or "cancel") Thread.Sleep(Timeout.Infinite);
        return 42;
    }

    /// <summary>
    /// Launches the real descendant probe with inherited standard streams and a private environment.
    /// </summary>
    /// <param name="executable">The test executable.</param>
    /// <param name="record">The process record path.</param>
    /// <param name="ready">The readiness path for the leaf process.</param>
    /// <param name="grandchild">Whether this process should launch a leaf and then exit.</param>
    /// <returns>The started process.</returns>
    public static Process Start(string executable, string record, string ready, bool grandchild)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName~ComparisonDescendantTests.RunDescendantProbe");
        start.Environment["ILREPL_DESCENDANT_RECORD"] = record;
        start.Environment["ILREPL_DESCENDANT_READY"] = ready;
        start.Environment["ILREPL_DESCENDANT_BRANCH"] = grandchild.ToString();
        return Process.Start(start) ?? throw new InvalidOperationException("the descendant did not start");
    }

    /// <summary>
    /// Checks a recorded process without relying on its original parent remaining alive.
    /// </summary>
    /// <param name="pid">The process identifier.</param>
    /// <returns>Whether the process is still running.</returns>
    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
