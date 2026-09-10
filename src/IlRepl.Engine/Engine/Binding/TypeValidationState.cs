namespace IlRepl.Engine.Binding;

/// <summary>
/// Supplies declaration facts that supplement member metadata when validating a completed type.
/// </summary>
/// <param name="Type">The definition being validated.</param>
/// <param name="Name">The full IL name.</param>
/// <param name="Kind">The declared kind.</param>
/// <param name="Layout">The layout attribute.</param>
/// <param name="PackingSize">The declared packing size.</param>
/// <param name="ClassSize">The declared minimum size.</param>
/// <param name="Offsets">Offsets of declared instance fields.</param>
/// <param name="Parameters">The definition's generic parameters and constraints.</param>
/// <param name="Overrides">Explicitly implemented method slots.</param>
internal sealed record TypeValidationState(
    TypeSymbol Type,
    string Name,
    TypeKind Kind,
    TypeLayoutKind Layout,
    int? PackingSize,
    int? ClassSize,
    IReadOnlyDictionary<string, int?> Offsets,
    IReadOnlyList<GenericParameterSymbol> Parameters,
    IReadOnlyList<MethodSymbol> Overrides)
{
    /// <summary>
    /// The IL kind used in declaration diagnostics.
    /// </summary>
    public string KindWord => Kind switch
    {
        TypeKind.Struct => "struct", TypeKind.Interface => "interface", TypeKind.Enum => "enum", _ => "class",
    };

    /// <summary>
    /// The kind and full name used to identify this declaration in a diagnostic.
    /// </summary>
    public string Description => KindWord + " " + Name;
}
