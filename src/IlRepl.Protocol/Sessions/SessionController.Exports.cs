namespace IlRepl.Protocol;

/// <summary>
/// Coordinates document actions and frontend-specific export delivery around a replaceable engine.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>
    /// Delivers exported assembly bytes through a frontend-specific download destination.
    /// </summary>
    public Func<AssemblyExportResult, CancellationToken, Task>? ExportAsync { get; set; }
}
