using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Keeps a document dialog separate from the editor so cancelling it preserves the entire draft.
/// </summary>
public sealed class SessionDialog
{
    /// <summary>
    /// Whether the dialog has requested its initial focus.
    /// </summary>
    public bool Focused { get; set; }

    /// <summary>
    /// Whether a choice was submitted while the dialog remains visible until its operation completes.
    /// </summary>
    public bool Submitted { get; set; }

    /// <summary>
    /// Whether this dialog requests a file path rather than an unsaved-source decision.
    /// </summary>
    public bool IsPath { get; init; }

    /// <summary>
    /// Whether the requested path will be opened rather than saved.
    /// </summary>
    public bool Opening { get; init; }

    /// <summary>
    /// The path being edited independently of the IL source.
    /// </summary>
    public string Path { get; set; } = "session.ilrepl.json";

    /// <summary>
    /// The selected file path, or null when the dialog was cancelled.
    /// </summary>
    public TaskCompletionSource<string?> PathResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The user's decision about unsaved source.
    /// </summary>
    public TaskCompletionSource<SessionDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
