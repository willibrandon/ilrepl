namespace IlRepl.Engine;

/// <summary>
/// One accepted line of a cell after parsing: an instruction with its labels, a block boundary,
/// or a declaration.
/// </summary>
public sealed class CellEntry
{
    /// <summary>
    /// What the entry represents.
    /// </summary>
    public required EntryKind Kind { get; init; }

    /// <summary>
    /// The source line, as accepted.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Labels defined on this line, in order.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>
    /// The instruction, for <see cref="EntryKind.Instruction"/> entries.
    /// </summary>
    public Instruction? Instruction { get; init; }

    /// <summary>
    /// The block boundary, for <see cref="EntryKind.Block"/> entries.
    /// </summary>
    public BlockKind? Block { get; init; }

    /// <summary>
    /// The exception type of a <see cref="BlockKind.Catch"/> boundary.
    /// </summary>
    public Type? CatchType { get; init; }

    /// <summary>
    /// The locals declared by this line, for <see cref="EntryKind.Locals"/> entries.
    /// </summary>
    public IReadOnlyList<LocalDeclaration> Locals { get; init; } = [];

    /// <summary>
    /// The arguments declared by this line, for <see cref="EntryKind.Arguments"/> entries.
    /// </summary>
    public IReadOnlyList<ArgumentDeclaration> Arguments { get; init; } = [];
}
