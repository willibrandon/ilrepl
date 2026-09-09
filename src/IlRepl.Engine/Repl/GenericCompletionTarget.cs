using IlRepl.Engine.Binding;

namespace IlRepl.Repl;

/// <summary>
/// Retains the exact generic definition and any declaring construction chosen by a continuation.
/// </summary>
internal sealed record GenericCompletionTarget
{
    /// <summary>
    /// The type definition being constructed, or null for a method construction.
    /// </summary>
    public TypeSymbol? Type { get; init; }

    /// <summary>
    /// The method definition and its constructed declaring type, or null for a type construction.
    /// </summary>
    public MethodSymbol? Method { get; init; }

    /// <summary>
    /// The ordered generic parameters and their retained constraints.
    /// </summary>
    public required IReadOnlyList<GenericParameterSymbol> Parameters { get; init; }

    /// <summary>
    /// The complete owner label distinguishing overloads and arities.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Equivalent declaration identities after replay of an unchanged binding environment.
    /// </summary>
    public IReadOnlyDictionary<DefinitionId, TypeSymbol> Rebindings { get; init; } = new Dictionary<DefinitionId, TypeSymbol>();
}
