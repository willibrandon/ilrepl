namespace IlRepl.Protocol;

/// <summary>
/// What a stretch of a line of IL is, as far as comments and quoting go.
/// </summary>
public enum CilSegmentKind
{
    /// <summary>
    /// Ordinary text: opcodes, operands, directives, punctuation.
    /// </summary>
    Code,

    /// <summary>
    /// A double-quoted string literal, quotes included, with C escapes honoured.
    /// </summary>
    String,

    /// <summary>
    /// A single-quoted name, quotes included, as ILAsm allows around any identifier.
    /// </summary>
    QuotedName,

    /// <summary>
    /// A <c>//</c> comment running to the end of the line.
    /// </summary>
    LineComment,

    /// <summary>
    /// A <c>/* */</c> comment, or the part of one that falls on this line.
    /// </summary>
    BlockComment,
}
