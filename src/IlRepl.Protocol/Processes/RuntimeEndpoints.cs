namespace IlRepl.Protocol;

/// <summary>
/// Removes the debugger pipes and the diagnostics socket that a killed .NET runtime leaves in the temporary directory.
/// </summary>
/// <remarks>
/// On Unix the runtime creates these files when it starts and deletes them at an orderly exit. A killed runtime cannot, and
/// ilrepl ends its workers as soon as they have reported, so every comparison left six files behind for good. Enough of them
/// use up the inodes of a temporary directory that lives in memory, and then nothing on the machine can create a file there.
/// </remarks>
public static class RuntimeEndpoints
{
    /// <summary>
    /// Deletes the endpoints of a process that has ended.
    /// </summary>
    /// <param name="processId">The identifier the ended process had.</param>
    public static void Remove(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows uses named pipes, which end with their process.
            return;
        }

        try
        {
            foreach (var pattern in new[] { $"dotnet-diagnostic-{processId}-*", $"clr-debug-pipe-{processId}-*" })
            {
                foreach (var endpoint in Directory.EnumerateFiles(Path.GetTempPath(), pattern))
                {
                    File.Delete(endpoint);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another cleanup got there first, or the file belongs to someone else. Either way there is nothing left to do.
        }
    }
}
