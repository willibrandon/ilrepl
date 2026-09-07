using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A <c>.custom</c> attribute as semantic values: the constructor and its arguments, and the
/// named fields and properties. A blob written in the ildasm form is decoded into these at the
/// line, so the attribute can be written again against any assembly.
/// </summary>
/// <param name="Constructor">The attribute constructor.</param>
/// <param name="FixedArguments">The constructor arguments, typed by the constructor's parameters.</param>
/// <param name="NamedFields">The named field arguments.</param>
/// <param name="NamedProperties">The named property arguments.</param>
/// <param name="Source">The line as typed.</param>
public sealed record CustomAttributeDeclaration(
    ConstructorInfo Constructor,
    IReadOnlyList<object?> FixedArguments,
    IReadOnlyList<(FieldInfo Field, object? Value)> NamedFields,
    IReadOnlyList<(PropertyInfo Property, object? Value)> NamedProperties,
    string Source)
{
    /// <summary>
    /// The attribute type.
    /// </summary>
    public Type AttributeType => Constructor.DeclaringType!;

    /// <summary>
    /// Renders the attribute the way a listing shows it.
    /// </summary>
    /// <returns>The description, for example <c>[FlagsAttribute]</c> or <c>[ObsoleteAttribute("old")]</c>.</returns>
    public string Describe()
    {
        var arguments = FixedArguments.Select(ConstantText.Describe)
            .Concat(NamedFields.Select(f => f.Field.Name + " = " + ConstantText.Describe(f.Value)))
            .Concat(NamedProperties.Select(p => p.Property.Name + " = " + ConstantText.Describe(p.Value)))
            .ToList();
        return "[" + TypeNameFormatter.Pretty(AttributeType) + (arguments.Count == 0 ? "" : "(" + string.Join(", ", arguments) + ")") + "]";
    }
}
