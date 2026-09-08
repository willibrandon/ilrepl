namespace IlRepl.Protocol;

/// <summary>
/// One stretch of a line of IL: where it starts, how long it is, and what it is.
/// </summary>
/// <param name="Start">The offset of the first character.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="Kind">What the stretch is.</param>
public readonly record struct CilSegment(int Start, int Length, CilSegmentKind Kind)
{
    /// <summary>
    /// The offset just past the last character.
    /// </summary>
    public int End => Start + Length;
}
