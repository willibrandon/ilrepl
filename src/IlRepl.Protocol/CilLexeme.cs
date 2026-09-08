namespace IlRepl.Protocol;

/// <summary>
/// One stretch of a line as the scanner cut it, before its role is known.
/// </summary>
/// <param name="Start">The offset of the first character.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="Kind">What the scanner made of it.</param>
internal readonly record struct CilLexeme(int Start, int Length, CilLexemeKind Kind)
{
    /// <summary>
    /// The offset just past the last character.
    /// </summary>
    public int End => Start + Length;
}
