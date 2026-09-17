using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// What happened when the REPL handled a line.
/// </summary>
/// <param name="Succeeded">False when the line produced an error.</param>
/// <param name="QuitRequested">True when the line asked to leave.</param>
public sealed record HandleResult(bool Succeeded, bool QuitRequested)
{
    /// <summary>
    /// The typed session action awaiting frontend coordination.
    /// </summary>
    public SessionAction? SessionAction { get; init; }

    /// <summary>
    /// Source findings retained when the submitted line is refused.
    /// </summary>
    public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// The complete method document requested by .edit.
    /// </summary>
    public EditDocument? EditDocument { get; init; }

    /// <summary>
    /// The instruction and stack comparison requested by .diff.
    /// </summary>
    public EditDiff? Diff { get; init; }

    /// <summary>
    /// The immutable package awaiting execution by the frontend's isolated-runtime coordinator.
    /// </summary>
    internal ComparisonPackage? ComparisonPackage { get; init; }

    /// <summary>
    /// The captured implementation awaiting isolated compilation.
    /// </summary>
    internal NativePackage? NativePackage { get; init; }
}
