using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using IlRepl.Processes;
using IlRepl.Protocol;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Launches real console clients and sends Windows control events without attaching the test runner's console.
/// </summary>
internal static partial class WindowsConsoleProbe
{
    private static readonly TaskCompletionSource BreakReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string? _closeMarker;

    /// <summary>
    /// Runs one isolated launcher or signal sender and returns its actual exit status.
    /// </summary>
    /// <param name="args">The private probe mode and its process or file arguments.</param>
    /// <returns>The launched frontend's status, or zero after the signal sender observes its event.</returns>
    internal static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows console events require Windows.");
        if (args[1] == "launch")
        {
            Publish(args[2] + ".launcher", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            try
            {
                InstallHandler();
                var start = new ProcessStartInfo(HostLocator.FindDotnet()) { UseShellExecute = false, RedirectStandardError = true };
                foreach (var argument in args[3..]) start.ArgumentList.Add(argument);
                using var frontend = Process.Start(start) ?? throw new InvalidOperationException("The frontend did not start.");
                var errors = frontend.StandardError.ReadToEndAsync();
                Publish(args[2], frontend.Id.ToString(CultureInfo.InvariantCulture));
                try
                {
                    await OwnedProcessGroup.WaitForExitAsync(frontend, CancellationToken.None);
                    Publish(args[2] + ".stderr", await errors);
                    Publish(args[2] + ".exit", frontend.ExitCode.ToString(CultureInfo.InvariantCulture));
                    return frontend.ExitCode;
                }
                finally
                {
                    if (!frontend.HasExited) frontend.Kill(entireProcessTree: true);
                    await OwnedProcessGroup.WaitForExitAsync(frontend, CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                Publish(args[2] + ".error", exception.ToString());
                throw;
            }
        }

        _ = FreeConsole();
        if (AttachConsole(uint.Parse(args[2], CultureInfo.InvariantCulture)) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (args[1] == "observe-close") _closeMarker = args[3];
            InstallHandler();
            if (args[1] == "observe-close")
            {
                Publish(args[4], "ready");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            }
            if (GenerateConsoleCtrlEvent(1, 0) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            await BreakReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));
            return 0;
        }
        finally { _ = FreeConsole(); }
    }

    private static unsafe void InstallHandler()
    {
        if (SetConsoleCtrlHandler(&HandleControl, 1) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static void Publish(string path, string contents)
    {
        var temporary = path + ".pending";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path);
    }

    [UnmanagedCallersOnly]
    private static int HandleControl(uint control)
    {
        if (control == 1)
        {
            BreakReceived.TrySetResult();
            return 1;
        }
        if (control == 2 && _closeMarker is { } marker)
        {
            // A control callback must never propagate a managed exception across the native boundary.
            try { Publish(marker, "CTRL_CLOSE_EVENT"); }
            catch { return 0; }
        }
        return 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler, int add);
}
