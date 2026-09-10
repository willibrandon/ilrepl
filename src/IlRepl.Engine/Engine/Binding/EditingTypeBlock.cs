namespace IlRepl.Engine.Binding;

/// <summary>
/// A symbolic class block whose declarations never own a runtime prototype.
/// </summary>
internal sealed class EditingTypeBlock
{
    /// <summary>
    /// The immutable state before the header, used when abandoning or replaying the open class.
    /// </summary>
    public required EditingState Before { get; init; }

    /// <summary>
    /// The parsed header and type flags.
    /// </summary>
    public required TypeHeader Header { get; init; }

    /// <summary>
    /// The original header line.
    /// </summary>
    public required string HeaderLine { get; init; }

    /// <summary>
    /// The declared type identity.
    /// </summary>
    public required TypeSymbol Type { get; init; }

    /// <summary>
    /// The full IL path, including enclosing types.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The header's actual class, struct, interface or enum kind.
    /// </summary>
    public required TypeKind Kind { get; init; }

    /// <summary>
    /// Whether the opening brace has been seen.
    /// </summary>
    public bool BraceSeen { get; set; }

    /// <summary>
    /// The packing size supplied by .pack.
    /// </summary>
    public int? PackingSize { get; set; }

    /// <summary>
    /// The class size supplied by .size.
    /// </summary>
    public int? ClassSize { get; set; }

    /// <summary>
    /// All lines in the block, including nested declarations.
    /// </summary>
    public List<string> Lines { get; private set; } = [];

    /// <summary>
    /// The bodies of methods that have closed inside the block.
    /// </summary>
    public List<EditingBody> Bodies { get; private set; } = [];

    /// <summary>
    /// The bound field declarations and their retained constants.
    /// </summary>
    public List<FieldDeclarationSyntax> Fields { get; private set; } = [];

    /// <summary>
    /// The closed property and event declarations, validated with the complete class.
    /// </summary>
    public List<EditingAccessorBlock> Accessors { get; private set; } = [];

    /// <summary>
    /// The explicit slot mappings written at class level.
    /// </summary>
    public List<OverrideSymbol> Overrides { get; private set; } = [];

    /// <summary>
    /// Types referenced by metadata directives on this class and its fields.
    /// </summary>
    public List<TypeSymbol> MetadataTypes { get; private set; } = [];

    /// <summary>
    /// Session methods referenced by nested types that have already closed.
    /// </summary>
    public List<string> NestedMethodReferences { get; private set; } = [];

    /// <summary>
    /// Completed nested declarations whose hierarchy is checked when the complete family closes.
    /// </summary>
    public List<TypeValidationState> NestedValidations { get; private set; } = [];

    /// <summary>
    /// Nested member bodies whose forward references are rechecked against the completed family.
    /// </summary>
    public List<EditingBody> NestedBodies { get; private set; } = [];

    /// <summary>
    /// Copies a block's mutable records without changing its identities.
    /// </summary>
    /// <returns>The independent checkpoint.</returns>
    public EditingTypeBlock Clone()
    {
        var clone = (EditingTypeBlock)MemberwiseClone();
        clone.Lines = [.. Lines];
        clone.Bodies = [.. Bodies.Select(body => body.Clone())];
        clone.Fields = [.. Fields];
        clone.Accessors = [.. Accessors.Select(accessor => accessor.Clone())];
        clone.Overrides = [.. Overrides];
        clone.MetadataTypes = [.. MetadataTypes];
        clone.NestedMethodReferences = [.. NestedMethodReferences];
        clone.NestedValidations = [.. NestedValidations];
        clone.NestedBodies = [.. NestedBodies.Select(body => body.Clone())];
        return clone;
    }
}
