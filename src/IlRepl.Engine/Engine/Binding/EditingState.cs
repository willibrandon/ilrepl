namespace IlRepl.Engine.Binding;

/// <summary>
/// Transactional session structure whose copies contain no mutable runtime records.
/// </summary>
internal sealed class EditingState
{
    /// <summary>
    /// The current committed type table with the open family overlaid when binding its body.
    /// </summary>
    public required SnapshotTypeTable Types { get; set; }

    /// <summary>
    /// The committed table used by inspection while a replacement remains open.
    /// </summary>
    public required SnapshotTypeTable CommittedTypes { get; set; }

    /// <summary>
    /// The session methods currently in scope.
    /// </summary>
    public List<MethodSymbol> Methods { get; private set; } = [];

    /// <summary>
    /// The source recipes of committed and speculatively accepted definitions.
    /// </summary>
    public List<EditingDefinition> Definitions { get; private set; } = [];

    /// <summary>
    /// The persistent cell declaration lines.
    /// </summary>
    public List<string> CellDeclarations { get; private set; } = [];

    /// <summary>
    /// The suspended or active cell body.
    /// </summary>
    public EditingBody Cell { get; set; } = new();

    /// <summary>
    /// The method being edited, or null at cell or class level.
    /// </summary>
    public EditingBody? Method { get; set; }

    /// <summary>
    /// The open property or event block, or null outside an accessor declaration.
    /// </summary>
    public EditingAccessorBlock? Accessor { get; set; }

    /// <summary>
    /// The open type chain, outermost first.
    /// </summary>
    public List<EditingTypeBlock> OpenTypes { get; private set; } = [];

    /// <summary>
    /// Whether normalization is inside a multiline comment.
    /// </summary>
    public bool InBlockComment { get; set; }

    /// <summary>
    /// Whether a quit command ended this replay.
    /// </summary>
    public bool Ended { get; set; }

    /// <summary>
    /// The concrete arguments assigned to the cell's generic parameters, or null before an assignment.
    /// </summary>
    public IReadOnlyList<TypeSymbol>? TypeArguments { get; set; }

    /// <summary>
    /// The monotonically increasing identity used for label spaces within this editing state.
    /// </summary>
    public long NextBody { get; set; } = 1;

    /// <summary>
    /// The active instruction body.
    /// </summary>
    public EditingBody Body => Method ?? Cell;

    /// <summary>
    /// Copies the complete state before a line that can be refused.
    /// </summary>
    /// <returns>An independent checkpoint preserving every existing symbol identity.</returns>
    public EditingState Clone()
    {
        var clone = (EditingState)MemberwiseClone();
        clone.Types = Types.Clone();
        clone.CommittedTypes = CommittedTypes.Clone();
        clone.Methods = [.. Methods];
        clone.Definitions = [.. Definitions];
        clone.CellDeclarations = [.. CellDeclarations];
        clone.Cell = Cell.Clone();
        clone.Method = Method?.Clone();
        clone.Accessor = Accessor?.Clone();
        clone.OpenTypes = [.. OpenTypes.Select(type => type.Clone())];
        return clone;
    }
}
