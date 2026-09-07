namespace IlRepl.Engine;

/// <summary>
/// One line of a disassembly listing.
/// </summary>
/// <param name="Kind">What the line is.</param>
/// <param name="Offset">The byte offset the line belongs to: the instruction's, or the offset a label or boundary sits before.</param>
public sealed record DisassembledEntry(DisassembledEntryKind Kind, int Offset)
{
    /// <summary>
    /// The label name for a label line.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The instruction for an instruction line, with its text already rendered.
    /// </summary>
    public Instruction? Instruction { get; init; }

    /// <summary>
    /// The text of a raw line.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// The boundary kind for a block line.
    /// </summary>
    public BlockKind? Block { get; init; }

    /// <summary>
    /// The catch type for a catch boundary, or null when it did not resolve.
    /// </summary>
    public Type? CatchType { get; init; }

    /// <summary>
    /// The catch type as the metadata spells it, for the boundary text.
    /// </summary>
    public string? CatchText { get; init; }

    /// <summary>
    /// True when the stack effect of this line cannot be modeled: an operand that did not
    /// resolve, or a prefix the simulator does not know. The analysis loses the stack from here.
    /// </summary>
    public bool EffectUnknown { get; init; }

    /// <summary>
    /// The decoded instruction behind an instruction or raw line.
    /// </summary>
    public RawInstruction? Raw { get; init; }

    /// <summary>
    /// The text to print for an instruction or raw line.
    /// </summary>
    public string DisplayText => Instruction?.Text ?? Text ?? "";
}
