using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// One generic parameter of a <c>.class</c> or of a generic <c>.method</c> inside one: its
/// name, its variance and special constraints, and the types it is constrained to.
/// </summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Attributes">Variance and the <c>class</c>, <c>valuetype</c>, and <c>.ctor</c> constraints.</param>
/// <param name="Constraints">The base class and interfaces the parameter must satisfy.</param>
public sealed record GenericParameterDeclaration(string Name, GenericParameterAttributes Attributes, IReadOnlyList<Type> Constraints)
{
    /// <summary>
    /// Renders the parameter as ILAsm writes it inside the angle brackets.
    /// </summary>
    /// <returns>The declaration text, for example <c>+(class IComparable) T</c>.</returns>
    public string Describe()
    {
        var words = new List<string>();
        if (Attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
        {
            words.Add("class");
        }

        if (Attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
        {
            words.Add("valuetype");
        }

        if (Attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint))
        {
            words.Add(".ctor");
        }

        words.AddRange(Constraints.Select(TypeNameFormatter.Pretty));
        var variance = Attributes.HasFlag(GenericParameterAttributes.Covariant) ? "+" : Attributes.HasFlag(GenericParameterAttributes.Contravariant) ? "-" : "";
        var constraints = words.Count == 0 ? "" : "(" + string.Join(", ", words) + ") ";
        return variance + constraints + Name;
    }
}
