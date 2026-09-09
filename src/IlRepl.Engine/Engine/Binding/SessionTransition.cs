namespace IlRepl.Engine.Binding;

/// <summary>
/// The structural effect a command has on the session, apart from what it prints.
/// </summary>
public enum SessionTransition
{
    /// <summary>
    /// The command is unknown; the handler refuses it.
    /// </summary>
    Unknown,

    /// <summary>
    /// The command prints or toggles something and changes no declaration, body, or block.
    /// </summary>
    None,

    /// <summary>
    /// The session ends.
    /// </summary>
    Quit,

    /// <summary>
    /// The cell runs and is cleared, once no block is open.
    /// </summary>
    Run,

    /// <summary>
    /// The last accepted line is taken back; a block whose header goes is abandoned.
    /// </summary>
    Undo,

    /// <summary>
    /// The open method is abandoned, else the open class, else the cell is cleared and declarations kept.
    /// </summary>
    Clear,

    /// <summary>
    /// The cell, declarations, methods, and types are all cleared.
    /// </summary>
    Reset,

    /// <summary>
    /// An assembly is loaded; names that failed before may resolve after it.
    /// </summary>
    Load,

    /// <summary>
    /// The session is written to a file, once no block is open; nothing in it changes.
    /// </summary>
    Save,
}
