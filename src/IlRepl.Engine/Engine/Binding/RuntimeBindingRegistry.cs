namespace IlRepl.Engine.Binding;

/// <summary>
/// Retains runtime payloads exclusively within the scope that binds accepted input.
/// </summary>
internal sealed class RuntimeBindingRegistry
{
    /// <summary>
    /// Runtime types registered under their complete symbolic identities.
    /// </summary>
    public Dictionary<TypeSymbol, Type> Types { get; } = [];

    /// <summary>
    /// Loaded method payloads keyed by their complete constructed signatures.
    /// </summary>
    public Dictionary<MethodSymbol, object> Methods { get; } = [];

    /// <summary>
    /// Loaded field payloads keyed by their complete constructed signatures.
    /// </summary>
    public Dictionary<FieldSymbol, object> Fields { get; } = [];

    /// <summary>
    /// Prototype payloads keyed by definition identity.
    /// </summary>
    public Dictionary<DefinitionId, object> Declarations { get; } = [];

    /// <summary>
    /// The copied signatures of accepted session methods.
    /// </summary>
    public IReadOnlyList<MethodSymbol>? SessionMethods { get; set; }

    /// <summary>
    /// The copied local variable signatures.
    /// </summary>
    public IReadOnlyList<VariableSymbol>? Locals { get; set; }

    /// <summary>
    /// The copied argument signatures.
    /// </summary>
    public IReadOnlyList<VariableSymbol>? Arguments { get; set; }
}
