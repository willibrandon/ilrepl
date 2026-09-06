namespace IlRepl.Engine;

/// <summary>
/// What a line added to a cell turned out to be.
/// </summary>
public enum LineOutcome
{
    /// <summary>
    /// Whitespace or a comment; nothing was added.
    /// </summary>
    Empty,

    /// <summary>
    /// One or more labels with no instruction.
    /// </summary>
    Labels,

    /// <summary>
    /// An instruction.
    /// </summary>
    Instruction,

    /// <summary>
    /// An exception-handling block boundary.
    /// </summary>
    Block,

    /// <summary>
    /// A <c>.locals</c> declaration.
    /// </summary>
    Locals,

    /// <summary>
    /// An <c>.args</c> declaration.
    /// </summary>
    Arguments,

    /// <summary>
    /// A <c>.typeparams</c> declaration.
    /// </summary>
    TypeParameters,

    /// <summary>
    /// The <c>.vararg</c> marker.
    /// </summary>
    VarArg,

    /// <summary>
    /// A <c>.typeargs</c> binding for the next run.
    /// </summary>
    TypeArguments,
}
