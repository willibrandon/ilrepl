namespace IlRepl.Tui;

/// <summary>
/// Keeps terminal interrupt presentation separate from source editing and pending submissions.
/// </summary>
public sealed partial class PromptState
{
    /// <summary>
    /// The phase-aware interrupt state used by key bindings and completed frames.
    /// </summary>
    internal InterruptState Interruption { get; } = new();

    /// <summary>
    /// Consumes an interrupt when execution or a recent interruption owns the key.
    /// </summary>
    internal Func<bool>? Interrupt { get; set; }
}
