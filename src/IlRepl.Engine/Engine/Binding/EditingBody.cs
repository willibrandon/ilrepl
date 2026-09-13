namespace IlRepl.Engine.Binding;

/// <summary>
/// An independently editable method or cell body holding only symbols and source text.
/// </summary>
internal sealed class EditingBody
{
    /// <summary>
    /// The immutable state before the header, used when abandoning or replaying an open method.
    /// </summary>
    public EditingState? Before { get; init; }

    /// <summary>
    /// The declared signature, or null for the cell.
    /// </summary>
    public MethodSymbol? Signature { get; set; }

    /// <summary>
    /// The original method header, or null for the cell.
    /// </summary>
    public string? Header { get; set; }

    /// <summary>
    /// The generic parameters in scope.
    /// </summary>
    public SymbolGenericContext Generics { get; set; } = SymbolGenericContext.Empty;

    /// <summary>
    /// The lexical access scope of the body.
    /// </summary>
    public AccessContext Access { get; set; } = AccessContext.Cell;

    /// <summary>
    /// The receiver slot, or -1 outside an instance method.
    /// </summary>
    public int ThisIndex { get; set; } = -1;

    /// <summary>
    /// Whether the opening method brace has been accepted.
    /// </summary>
    public bool BraceSeen { get; set; }

    /// <summary>
    /// Whether a following brace belongs to a previously accepted region header.
    /// </summary>
    public bool RegionBracePending { get; set; }

    /// <summary>
    /// Whether the cell or member has the vararg calling convention.
    /// </summary>
    public bool IsVarArg { get; set; }

    /// <summary>
    /// Whether the last accepted entry ends control flow.
    /// </summary>
    public bool EndsFlow { get; set; }

    /// <summary>
    /// The identity of this body's label space.
    /// </summary>
    public long LabelSpace { get; set; }

    /// <summary>
    /// Identifies transactional forms of one body during document analysis.
    /// </summary>
    public object AnalysisIdentity { get; init; } = new();

    /// <summary>
    /// The index of the active parameter metadata target, including zero for the return value.
    /// </summary>
    public int? ParameterTarget { get; set; }

    /// <summary>
    /// The local slots in declaration order.
    /// </summary>
    public List<VariableSymbol> Locals { get; private set; } = [];

    /// <summary>
    /// The argument slots in declaration order.
    /// </summary>
    public List<VariableSymbol> Arguments { get; private set; } = [];

    /// <summary>
    /// The accepted source lines, excluding the method header.
    /// </summary>
    public List<string> Lines { get; private set; } = [];

    /// <summary>
    /// The accepted instructions, for prefixes and dependency analysis.
    /// </summary>
    public List<BoundInstruction> Instructions { get; private set; } = [];

    /// <summary>
    /// Bound source entries retained for control-flow analysis.
    /// </summary>
    public List<FlowNode<TypeSymbol>> FlowNodes { get; private set; } = [];

    /// <summary>
    /// The latest analysis of this body's source entries.
    /// </summary>
    public FlowResult<TypeSymbol>? Analysis { get; set; }

    /// <summary>
    /// The explicit slot mappings declared on this method.
    /// </summary>
    public List<OverrideSymbol> Overrides { get; private set; } = [];

    /// <summary>
    /// Types referenced by metadata directives and handler clauses.
    /// </summary>
    public List<TypeSymbol> MetadataTypes { get; private set; } = [];

    /// <summary>
    /// The declared labels in this body.
    /// </summary>
    public HashSet<string> Labels { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The branch targets referenced in this body.
    /// </summary>
    public HashSet<string> ReferencedLabels { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The active protected-region handlers, outermost first.
    /// </summary>
    public List<BlockKind> Frames { get; private set; } = [];

    /// <summary>
    /// The stack after the last accepted entry.
    /// </summary>
    public EditingStack Stack { get; private set; } = new();

    /// <summary>
    /// Whether any branch references an undefined label.
    /// </summary>
    public bool HasPendingLabels => ReferencedLabels.Any(label => !Labels.Contains(label));

    /// <summary>
    /// Copies the complete body state for a transactional edit.
    /// </summary>
    /// <returns>An independent checkpoint.</returns>
    public EditingBody Clone()
    {
        var clone = (EditingBody)MemberwiseClone();
        clone.Locals = [.. Locals];
        clone.Arguments = [.. Arguments];
        clone.Lines = [.. Lines];
        clone.Instructions = [.. Instructions];
        clone.FlowNodes = [.. FlowNodes];
        clone.Overrides = [.. Overrides];
        clone.MetadataTypes = [.. MetadataTypes];
        clone.Labels = new HashSet<string>(Labels, StringComparer.Ordinal);
        clone.ReferencedLabels = new HashSet<string>(ReferencedLabels, StringComparer.Ordinal);
        clone.Frames = [.. Frames];
        clone.Stack = Stack.Clone();
        return clone;
    }
}
