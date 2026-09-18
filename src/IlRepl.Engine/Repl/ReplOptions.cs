namespace IlRepl.Repl;

/// <summary>
/// Settings that change how the REPL reports.
/// </summary>
public sealed class ReplOptions
{
    /// <summary>
    /// Whether this host can restore dependencies locally rather than requiring an embedded document from terminal ilrepl.
    /// </summary>
    public bool SupportsDependencyRestore { get; init; } = !OperatingSystem.IsBrowser();

    /// <summary>
    /// Show the simulated stack after each instruction.
    /// </summary>
    public bool EchoStack { get; set; } = true;

    /// <summary>
    /// Append how long each cell took to its result line.
    /// </summary>
    public bool ShowTiming { get; set; }

    /// <summary>
    /// The most transcript lines to keep.
    /// </summary>
    public int MaxTranscriptLines { get; set; } = 2000;
}
