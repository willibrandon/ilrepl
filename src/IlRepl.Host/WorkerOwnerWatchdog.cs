using System.Diagnostics;
using System.Globalization;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Stops an isolated worker group if its host disappears during supervisor adoption or startup.
/// </summary>
internal static class WorkerOwnerWatchdog
{
    private const string Variable = "ILREPL_WORKER_OWNER";

    /// <summary>
    /// Adds the launching host's stable identity to a worker's private startup environment.
    /// </summary>
    /// <param name="start">The worker launch configuration.</param>
    internal static void Configure(ProcessStartInfo start)
    {
        using var owner = Process.GetCurrentProcess();
        start.Environment[Variable] = string.Create(CultureInfo.InvariantCulture,
            $"{owner.Id}:{OwnedProcessGroup.GetStartIdentity(owner)}");
    }

    /// <summary>
    /// Starts a dedicated owner monitor before the worker can enter user code.
    /// </summary>
    internal static void Start()
    {
        var identity = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, null);
        if (OperatingSystem.IsWindows() || identity is null)
        {
            return;
        }

        var pieces = identity.Split(':');
        if (pieces.Length != 2 || !int.TryParse(pieces[0], out var processId) || !long.TryParse(pieces[1], out var started))
        {
            throw new InvalidDataException("invalid worker ownership identity");
        }

        var thread = new Thread(() =>
        {
            try
            {
                using var owner = Process.GetProcessById(processId);
                var scope = new OwnedProcessScope("owner", processId, started, null);
                OwnedProcessGroup.WaitForExitAsync(scope, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (ArgumentException)
            {
                // The owner is already gone, which is what this thread waits for.
            }

            using var group = new OwnedProcessGroup();
            group.Adopt(Environment.ProcessId);
            group.StopAsync().GetAwaiter().GetResult();
            Environment.Exit(3);
        }) { IsBackground = true, Name = "ilrepl worker owner" };

        thread.Start();
    }
}
