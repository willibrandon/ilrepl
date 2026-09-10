namespace IlRepl.Engine.Binding;

/// <summary>
/// Identifies a line's meaning before execution in both live and speculative handling.
/// </summary>
/// <remarks>
/// What one line means to the REPL before anything runs: the structural reading both the real
/// handler and a preview of the buffer share.
/// </remarks>
public enum ReplLineKind
{
    /// <summary>
    /// A comment and nothing else; the lexical state may have changed, nothing else does.
    /// </summary>
    Comment,

    /// <summary>
    /// A blank line: with no block open, it runs the cell when the cell holds anything.
    /// </summary>
    Blank,

    /// <summary>
    /// A dotted word that is not a directive: a command with an argument.
    /// </summary>
    Command,

    /// <summary>
    /// <c>ret</c> inside an open method: an instruction of the method; only the brace ends the block.
    /// </summary>
    RetInMethod,

    /// <summary>
    /// <c>ret</c> at the top level while a forward label or a block is still open: an instruction of the cell.
    /// </summary>
    RetInline,

    /// <summary>
    /// <c>ret</c> at the top level with nothing pending: runs the cell.
    /// </summary>
    RetRuns,

    /// <summary>
    /// Anything else: a directive, an instruction, a header, or a brace, for the session to take.
    /// </summary>
    Line,
}
