namespace IlRepl.Engine.Binding;

/// <summary>
/// An open property or event declaration whose accessor references remain symbolic until class close.
/// </summary>
internal sealed class EditingAccessorBlock
{
    /// <summary>
    /// The property header, or null for an event.
    /// </summary>
    public PropertyHeaderSymbol? Property { get; init; }

    /// <summary>
    /// The event header, or null for a property.
    /// </summary>
    public EventHeaderSymbol? Event { get; init; }

    /// <summary>
    /// Whether the opening brace has been accepted.
    /// </summary>
    public bool BraceSeen { get; set; }

    /// <summary>
    /// The accessor declarations accepted in this block.
    /// </summary>
    public List<AccessorReferenceSymbol> Accessors { get; private set; } = [];

    /// <summary>
    /// The metadata types referenced by attributes in this block.
    /// </summary>
    public List<TypeSymbol> ReferencedTypes { get; private set; } = [];

    /// <summary>
    /// The declared property or event name.
    /// </summary>
    public string Name => Property?.Name ?? Event!.Name;

    /// <summary>
    /// The directive kind used in diagnostics.
    /// </summary>
    public string Word => Property is null ? "event" : "property";

    /// <summary>
    /// Creates an independent transactional copy of this accessor block.
    /// </summary>
    /// <returns>The copy.</returns>
    public EditingAccessorBlock Clone()
    {
        var clone = (EditingAccessorBlock)MemberwiseClone();
        clone.Accessors = [.. Accessors];
        clone.ReferencedTypes = [.. ReferencedTypes];
        return clone;
    }
}
