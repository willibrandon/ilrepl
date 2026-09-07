namespace IlRepl.Engine;

/// <summary>
/// What a <see cref="CellEntry"/> represents.
/// </summary>
public enum EntryKind
{
    /// <summary>
    /// An instruction, possibly preceded by labels.
    /// </summary>
    Instruction,

    /// <summary>
    /// Labels with no instruction on the same line.
    /// </summary>
    Labels,

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
    /// The <c>.vararg</c> marker.
    /// </summary>
    VarArg,

    /// <summary>
    /// An <c>.override</c> line inside a member method.
    /// </summary>
    Override,

    /// <summary>
    /// A <c>.param</c> line inside a member method.
    /// </summary>
    Param,

    /// <summary>
    /// A <c>.custom</c> line inside a member method.
    /// </summary>
    Custom,
}
