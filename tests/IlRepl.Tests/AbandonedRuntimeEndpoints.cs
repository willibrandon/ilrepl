using System.Diagnostics;
using System.Globalization;
using IlRepl.Protocol;

namespace IlRepl.Tests;

/// <summary>
/// Removes the runtime pipes and sockets of the processes that this run killed or crashed on purpose.
/// </summary>
/// <remarks>
/// A killed .NET runtime cannot delete what it created in the temporary directory. The suite kills frontends, supervisors,
/// hosts, and their descendants to test recovery, and over many runs those files once used up every inode of a temporary
/// directory that lives in memory. The product removes the files of the workers it ends; this takes the rest.
/// </remarks>
[TestClass]
public static class AbandonedRuntimeEndpoints
{
    /// <summary>
    /// Deletes every runtime endpoint whose process no longer exists.
    /// </summary>
    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var owners = Directory.EnumerateFiles(Path.GetTempPath(), "dotnet-diagnostic-*")
            .Concat(Directory.EnumerateFiles(Path.GetTempPath(), "clr-debug-pipe-*"))
            .Select(path => Path.GetFileName(path).Split('-')
                .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
                .FirstOrDefault(number => number > 0))
            .Where(owner => owner > 0).Distinct();
        foreach (var owner in owners.Where(owner => !IsRunning(owner)))
        {
            RuntimeEndpoints.Remove(owner);
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process has this identifier any more.
            return false;
        }
    }
}
