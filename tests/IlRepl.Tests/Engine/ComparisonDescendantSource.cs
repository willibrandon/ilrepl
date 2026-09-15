using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Compared code starts real background descendants and checks whether an earlier side left a process running.
/// </summary>
public static partial class ComparisonDescendantSource
{
    /// <summary>
    /// Starts a descendant that inherits the worker's pipes, then returns, exits, throws, or waits for termination.
    /// </summary>
    /// <param name="executable">The test executable that provides the child process entry.</param>
    /// <param name="records">The test-owned directory containing process and readiness records.</param>
    /// <param name="grandchild">Whether an intermediate child exits before the comparison worker.</param>
    /// <param name="mode">The way the compared method ends.</param>
    /// <param name="escape">Whether the child creates a new Unix session.</param>
    /// <returns>The normal result, or a distinct value if the previous side left a live descendant.</returns>
    public static int Run(string executable, string records, bool grandchild, string mode, bool escape)
    {
        var record = Path.Combine(records, "processes");
        if (File.Exists(record) && File.ReadAllLines(record).Any(IsRunning))
        {
            return -1;
        }

        var ready = Path.Combine(records, Guid.NewGuid().ToString("N"));
        using var process = Start(executable, record, ready, grandchild, escape);
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
    /// <param name="escape">Whether the child creates a new Unix session.</param>
    /// <returns>The started process.</returns>
    public static Process Start(string executable, string record, string ready, bool grandchild, bool escape)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName~ComparisonDescendantTests.RunDescendantProbe");
        start.Environment["ILREPL_DESCENDANT_RECORD"] = record;
        start.Environment["ILREPL_DESCENDANT_READY"] = ready;
        start.Environment["ILREPL_DESCENDANT_BRANCH"] = grandchild.ToString();
        start.Environment["ILREPL_DESCENDANT_ESCAPE"] = escape.ToString();
        return Process.Start(start) ?? throw new InvalidOperationException("the descendant did not start");
    }

    /// <summary>
    /// Checks a recorded process without relying on its original parent remaining alive.
    /// </summary>
    /// <param name="record">The process identifier and UTC creation ticks.</param>
    /// <returns>Whether the process is still running.</returns>
    public static bool IsRunning(string record)
    {
        using var process = Open(record);
        return process is not null && !process.HasExited;
    }

    /// <summary>
    /// Opens the recorded process only when its creation time still matches, excluding reused process identifiers.
    /// </summary>
    /// <param name="record">The process identifier and UTC creation ticks.</param>
    /// <returns>The matching process, or null when it has exited and its identifier is no longer assigned to it.</returns>
    public static Process? Open(string record)
    {
        try
        {
            var parts = record.Split(' ');
            var process = Process.GetProcessById(int.Parse(parts[0], CultureInfo.InvariantCulture));
            try
            {
                if (process.StartTime.ToUniversalTime().Ticks == long.Parse(parts[1], CultureInfo.InvariantCulture)) return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }

            process.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves the current probe into a new Unix session so process-group cleanup alone cannot reach it.
    /// </summary>
    public static void EscapeProcessGroup()
    {
        if (!OperatingSystem.IsWindows() && CreateSession() < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int CreateSession();
}
