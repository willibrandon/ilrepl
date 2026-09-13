using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace IlRepl.Engine;

/// <summary>
/// Retains prototype constraints without keeping collectible definition families alive.
/// </summary>
internal static class RuntimeGenericConstraints
{
    private static readonly ConditionalWeakTable<Type, GenericParameterDeclaration> s_declarations = new();

    /// <summary>
    /// Records constraints that reflection cannot yet read from a generic parameter builder.
    /// </summary>
    public static void Register(Type parameter, GenericParameterDeclaration declaration) =>
        s_declarations.AddOrUpdate(parameter, declaration);

    /// <summary>
    /// Reads the declared guarantees of a prototype or loaded generic parameter.
    /// </summary>
    public static FlowParameter<Type> Read(Type parameter)
    {
        s_declarations.TryGetValue(parameter, out var declaration);
        var attributes = declaration?.Attributes ?? parameter.GenericParameterAttributes;
        var constraints = declaration is not null ? declaration.Constraints
            : parameter is GenericTypeParameterBuilder ? [] : parameter.GetGenericParameterConstraints();
        var value = attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint);
        var reference = attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)
            || constraints.Any(type => !type.IsInterface && !type.IsGenericParameter && !type.IsValueType
                && type != typeof(object) && type != typeof(ValueType) && type != typeof(Enum));
        return new FlowParameter<Type>(reference, value, constraints);
    }
}
