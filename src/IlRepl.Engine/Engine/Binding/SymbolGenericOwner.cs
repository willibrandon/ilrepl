using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Identifies the owners and parameter metadata used to decode a generic signature.
/// </summary>
/// <remarks>
/// The owners a signature's <c>!N</c> and <c>!!N</c> refer to while metadata is decoded: the
/// type definition and, inside a method, the method definition, each with its parameters' names
/// and special constraints.
/// </remarks>
/// <param name="Type">The type definition, or <see cref="DefinitionId.None"/> outside a type.</param>
/// <param name="TypeParameters">The type's generic parameters.</param>
/// <param name="Method">The method definition, or <see cref="DefinitionId.None"/> outside a method.</param>
/// <param name="MethodParameters">The method's generic parameters.</param>
public sealed record SymbolGenericOwner(
    DefinitionId Type,
    IReadOnlyList<(string Name, GenericParameterAttributes Attributes)> TypeParameters,
    DefinitionId Method,
    IReadOnlyList<(string Name, GenericParameterAttributes Attributes)> MethodParameters)
{
    /// <summary>
    /// No owner: a signature outside any generic scope.
    /// </summary>
    public static SymbolGenericOwner None { get; } = new(DefinitionId.None, [], DefinitionId.None, []);

    /// <summary>
    /// The type parameter at a position, named by its declaration when the owner has one.
    /// </summary>
    /// <param name="index">The position.</param>
    /// <returns>The parameter symbol.</returns>
    public TypeSymbol TypeParameter(int index)
    {
        var (name, attributes) = index < TypeParameters.Count ? TypeParameters[index] : ("!" + SymbolRenderer.Number(index),
            GenericParameterAttributes.None);
        return TypeSymbol.Parameter(Type, false, index, name, attributes);
    }

    /// <summary>
    /// The method parameter at a position, named by its declaration when the owner has one.
    /// </summary>
    /// <param name="index">The position.</param>
    /// <returns>The parameter symbol.</returns>
    public TypeSymbol MethodParameter(int index)
    {
        var (name, attributes) = index < MethodParameters.Count ? MethodParameters[index] : ("!!" + SymbolRenderer.Number(index),
            GenericParameterAttributes.None);
        return TypeSymbol.Parameter(Method, true, index, name, attributes);
    }

    /// <summary>
    /// A copy with a method owner.
    /// </summary>
    /// <param name="method">The method definition.</param>
    /// <param name="parameters">Its generic parameters.</param>
    /// <returns>The owner.</returns>
    public SymbolGenericOwner WithMethod(DefinitionId method, IReadOnlyList<(string Name,
        GenericParameterAttributes Attributes)> parameters) =>
        this with { Method = method, MethodParameters = parameters };
}
