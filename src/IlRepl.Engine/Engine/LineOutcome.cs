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

    /// <summary>
    /// A <c>.method</c> header opened a method block.
    /// </summary>
    MethodStart,

    /// <summary>
    /// A <c>}</c> closed the open method block and committed the method.
    /// </summary>
    MethodEnd,

    /// <summary>
    /// A <c>.class</c> header opened a type block.
    /// </summary>
    TypeStart,

    /// <summary>
    /// A <c>}</c> closed a type block: a nested one, or the outermost, which committed the family.
    /// </summary>
    TypeEnd,

    /// <summary>
    /// A <c>.field</c> declaration.
    /// </summary>
    Field,

    /// <summary>
    /// A <c>.property</c> or <c>.event</c> header, an accessor line, or the <c>}</c> that closed the block.
    /// </summary>
    Accessor,

    /// <summary>
    /// An <c>.override</c> line.
    /// </summary>
    Override,

    /// <summary>
    /// A <c>.pack</c> or <c>.size</c> line.
    /// </summary>
    Layout,

    /// <summary>
    /// A <c>.custom</c> attribute.
    /// </summary>
    Custom,

    /// <summary>
    /// A <c>.param</c> line.
    /// </summary>
    Param,
}
