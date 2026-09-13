using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// A class-level <c>.override T::M with ...</c> line, resolved when the type closes.
/// </summary>
/// <param name="Target">The base or interface method being implemented.</param>
/// <param name="BodyName">The name of the method on this type that implements it.</param>
/// <param name="BodyReturnType">The implementing method's return type.</param>
/// <param name="BodyParameterTypes">The implementing method's parameter types.</param>
/// <param name="BodyIsStatic">True when the implementing method is static.</param>
/// <param name="TargetDescription">The target as a listing shows it.</param>
/// <param name="Source">The line as typed.</param>
public sealed record ClassOverrideDeclaration(
    MethodBase Target,
    string TargetDescription,
    string BodyName,
    Type BodyReturnType,
    IReadOnlyList<Type> BodyParameterTypes,
    bool BodyIsStatic,
    string Source)
{
    /// <summary>
    /// The complete implementing return type when its annotations cannot be represented by <see cref="BodyReturnType"/>.
    /// </summary>
    internal TypeSymbol? ExactBodyReturnType { get; init; }

    /// <summary>
    /// The complete implementing parameter types, with null where <see cref="BodyParameterTypes"/> is exact.
    /// </summary>
    internal IReadOnlyList<TypeSymbol?> ExactBodyParameterTypes { get; init; } = [];

    /// <summary>
    /// Whether a declared method has the complete implementing signature named by this mapping.
    /// </summary>
    internal bool Matches(MethodSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return signature.Name == BodyName && signature.IsStatic == BodyIsStatic
            && Same(signature.ReturnType, signature.ExactReturnType, BodyReturnType, ExactBodyReturnType)
            && signature.Parameters.Count == BodyParameterTypes.Count
            && signature.Parameters.Select((parameter, index) => Same(
                    parameter.Type, parameter.ExactType, BodyParameterTypes[index], ExactBodyParameterType(index)))
                .All(matches => matches);
    }

    /// <summary>
    /// The implementing signature as a diagnostic shows it.
    /// </summary>
    internal string DescribeBody()
    {
        var returnType = ExactBodyReturnType is null
            ? TypeNameFormatter.Pretty(BodyReturnType) : SymbolRenderer.Annotated(ExactBodyReturnType);
        var parameters = BodyParameterTypes.Select((type, index) => ExactBodyParameterType(index) is { } exact
            ? SymbolRenderer.Annotated(exact) : TypeNameFormatter.Pretty(type));
        return $"{(BodyIsStatic ? "static" : "instance")} {returnType} {BodyName}({string.Join(", ", parameters)})";
    }

    /// <summary>
    /// The complete implementing parameter type at an index, or null when its runtime type is exact.
    /// </summary>
    internal TypeSymbol? ExactBodyParameterType(int index) => index < ExactBodyParameterTypes.Count
        ? ExactBodyParameterTypes[index] : null;

    private static bool Same(Type first, TypeSymbol? firstExact, Type second, TypeSymbol? secondExact)
    {
        if (firstExact is null && secondExact is null)
        {
            return TypeIdentity.Equal(first, second);
        }

        return SymbolIdentity.Equal(
            firstExact ?? RuntimeSymbolImporter.Import(first),
            secondExact ?? RuntimeSymbolImporter.Import(second));
    }
}
