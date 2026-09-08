namespace IlRepl.Protocol;

/// <summary>
/// One lexeme of a line of IL with the style it reads as. Tokens are ordered, disjoint, and
/// inside the line; every character that is not whitespace is inside exactly one, and whitespace
/// is inside one only for a string, a quoted name, or a comment.
/// </summary>
/// <param name="Start">The offset of the first character.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="Style">The style.</param>
public readonly record struct CilToken(int Start, int Length, SpanStyle Style)
{
    /// <summary>
    /// The offset just past the last character.
    /// </summary>
    public int End => Start + Length;
}
