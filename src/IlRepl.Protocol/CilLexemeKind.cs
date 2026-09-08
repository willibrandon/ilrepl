namespace IlRepl.Protocol;

/// <summary>
/// What the scanner made of a stretch of a line, before its role is known.
/// </summary>
internal enum CilLexemeKind
{
    /// <summary>
    /// A name, an opcode, a directive, a keyword: letters, digits, and the characters a type name may hold.
    /// </summary>
    Word,

    /// <summary>
    /// A single-quoted name, quotes included.
    /// </summary>
    Quoted,

    /// <summary>
    /// A number: decimal, hexadecimal, binary, or floating-point, with an optional sign.
    /// </summary>
    Number,

    /// <summary>
    /// A double-quoted string, quotes included.
    /// </summary>
    String,

    /// <summary>
    /// A comment, or the part of one on this line.
    /// </summary>
    Comment,

    /// <summary>
    /// One punctuation character.
    /// </summary>
    Punctuation,

    /// <summary>
    /// The <c>::</c> between a type and its member.
    /// </summary>
    DoubleColon,

    /// <summary>
    /// The <c>...</c> of an array bound or a vararg sentinel.
    /// </summary>
    Ellipsis,

    /// <summary>
    /// A generic parameter: <c>!0</c>, <c>!!T</c>.
    /// </summary>
    GenericParameter,

    /// <summary>
    /// A bracketed assembly name such as <c>[System.Runtime]</c>, or a bracketed parameter attribute such as <c>[out]</c>.
    /// </summary>
    AssemblyHint,

    /// <summary>
    /// A character the grammar has no use for.
    /// </summary>
    Other,
}
