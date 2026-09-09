namespace IlRepl.Engine.Binding;

/// <summary>
/// The source and dependency identities needed to rebuild a committed or preview definition.
/// </summary>
/// <param name="Name">The family path or session method name.</param>
/// <param name="IsFamily">Whether this is an outermost type family.</param>
/// <param name="Header">The declaration header.</param>
/// <param name="Lines">The body lines, without the final closing brace.</param>
/// <param name="Identities">Every type identity belonging to this family, including retained prototype generations.</param>
/// <param name="ReferencedTypes">Types referenced by metadata or bodies.</param>
/// <param name="ReferencedMethods">Session methods referenced by bodies.</param>
internal sealed record EditingDefinition(
    string Name,
    bool IsFamily,
    string Header,
    IReadOnlyList<string> Lines,
    IReadOnlyList<TypeSymbol> Identities,
    IReadOnlyList<TypeSymbol> ReferencedTypes,
    IReadOnlySet<string> ReferencedMethods);
