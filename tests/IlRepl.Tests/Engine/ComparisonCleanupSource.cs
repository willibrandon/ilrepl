namespace IlRepl.Tests.Engine;

/// <summary>
/// Leaves real filesystem restrictions behind when a comparison worker completes or exits.
/// </summary>
public static class ComparisonCleanupSource
{
    /// <summary>
    /// Records the worker directory, prevents recursive deletion, and returns a value or exits the process.
    /// </summary>
    /// <param name="record">The test-owned file that records both worker directories.</param>
    /// <param name="crash">Whether the worker exits without writing a comparison result.</param>
    /// <returns>The value produced by a normally completed worker.</returns>
    public static int Run(string record, bool crash)
    {
        File.AppendAllText(record, Environment.CurrentDirectory + Environment.NewLine);
        var locked = Directory.CreateDirectory("locked");
        var path = Path.Combine(locked.FullName, "data.txt");
        File.WriteAllText(path, "worker data");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(locked.FullName, UnixFileMode.None);
        }

        if (crash)
        {
            Environment.Exit(23);
        }

        return 42;
    }
}
