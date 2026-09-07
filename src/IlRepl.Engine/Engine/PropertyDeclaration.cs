using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A property declared with <c>.property</c>: its signature and the accessor methods named by
/// <c>.get</c>, <c>.set</c>, and <c>.other</c>, which are ordinary methods of the same type.
/// </summary>
/// <param name="Name">The property name.</param>
/// <param name="Type">The property type.</param>
/// <param name="ParameterTypes">The index parameter types, empty for a plain property.</param>
/// <param name="IsStatic">True when the accessors are static.</param>
/// <param name="Attributes">The property attributes (<c>specialname</c>).</param>
/// <param name="Getter">The getter, or null.</param>
/// <param name="Setter">The setter, or null.</param>
/// <param name="Others">The other accessors.</param>
/// <param name="DefaultValue">A constant written after <c>=</c>, or null.</param>
/// <param name="HasDefault">True when a constant was written.</param>
/// <param name="CustomAttributes">The custom attributes declared on the property.</param>
/// <param name="HeaderLine">The <c>.property</c> line as typed.</param>
/// <param name="Lines">The lines of the block as typed.</param>
public sealed record PropertyDeclaration(
    string Name,
    Type Type,
    IReadOnlyList<Type> ParameterTypes,
    bool IsStatic,
    PropertyAttributes Attributes,
    MethodDeclaration? Getter,
    MethodDeclaration? Setter,
    IReadOnlyList<MethodDeclaration> Others,
    object? DefaultValue,
    bool HasDefault,
    IReadOnlyList<CustomAttributeDeclaration> CustomAttributes,
    string HeaderLine,
    IReadOnlyList<string> Lines)
{
    /// <summary>
    /// Renders the property the way a listing shows it, for example <c>property int32 Length { get }</c>.
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        var accessors = new List<string>();
        if (Getter is not null)
        {
            accessors.Add("get");
        }

        if (Setter is not null)
        {
            accessors.Add("set");
        }

        accessors.AddRange(Others.Select(o => o.Name));
        var parameters = ParameterTypes.Count == 0 ? "" : "(" + string.Join(", ", ParameterTypes.Select(TypeNameFormatter.Pretty)) + ")";
        return $"{(IsStatic ? "static " : "")}property {TypeNameFormatter.Pretty(Type)} {Name}{parameters} {{ {string.Join(", ", accessors)} }}";
    }
}
