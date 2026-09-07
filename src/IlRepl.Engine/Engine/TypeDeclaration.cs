using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A type declared with <c>.class</c>, frozen when its block closes: everything the writer needs
/// to emit it and everything a listing or an export shows. Nested types are declared inside it.
/// </summary>
/// <param name="Name">The metadata name, with its arity suffix for a generic type.</param>
/// <param name="Namespace">The namespace, or empty.</param>
/// <param name="FullName">The ILAsm path: <c>Geometry.Point</c>, <c>Outer/Inner</c>.</param>
/// <param name="Kind">Class, struct, interface, or enum.</param>
/// <param name="Attributes">The type attributes as declared.</param>
/// <param name="Layout">The field layout.</param>
/// <param name="TypeParameters">The generic parameters, redeclared ones first.</param>
/// <param name="BaseType">The base type, or null for an interface.</param>
/// <param name="Interfaces">The interfaces implemented.</param>
/// <param name="PackingSize">The <c>.pack</c> value, or null.</param>
/// <param name="ClassSize">The <c>.size</c> value, or null.</param>
/// <param name="Fields">The fields in declaration order.</param>
/// <param name="Methods">The methods, constructors, and type initializer in declaration order.</param>
/// <param name="Properties">The properties.</param>
/// <param name="Events">The events.</param>
/// <param name="NestedTypes">The nested types in declaration order.</param>
/// <param name="Overrides">The class-level <c>.override ... with</c> lines.</param>
/// <param name="CustomAttributes">The custom attributes declared on the type.</param>
/// <param name="HeaderLine">The <c>.class</c> line as typed.</param>
/// <param name="Lines">Every line after the header, nested blocks included, without the closing brace.</param>
public sealed record TypeDeclaration(
    string Name,
    string Namespace,
    string FullName,
    TypeKind Kind,
    TypeAttributes Attributes,
    TypeLayoutKind Layout,
    IReadOnlyList<GenericParameterDeclaration> TypeParameters,
    Type? BaseType,
    IReadOnlyList<Type> Interfaces,
    int? PackingSize,
    int? ClassSize,
    IReadOnlyList<FieldDeclaration> Fields,
    IReadOnlyList<MethodDeclaration> Methods,
    IReadOnlyList<PropertyDeclaration> Properties,
    IReadOnlyList<EventDeclaration> Events,
    IReadOnlyList<TypeDeclaration> NestedTypes,
    IReadOnlyList<ClassOverrideDeclaration> Overrides,
    IReadOnlyList<CustomAttributeDeclaration> CustomAttributes,
    string HeaderLine,
    IReadOnlyList<string> Lines)
{
    /// <summary>
    /// True when this type is nested in another.
    /// </summary>
    public bool IsNested => FullName.Contains('/');

    /// <summary>
    /// The word a listing uses for the kind.
    /// </summary>
    public string KindWord => Kind switch
    {
        TypeKind.Struct => "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        _ => "class",
    };

    /// <summary>
    /// The name shown in listings and the status bar, with generic parameters.
    /// </summary>
    public string DisplayName => TypeParameters.Count == 0
        ? FullName
        : FullName + "<" + string.Join(", ", TypeParameters.Select(p => p.Name)) + ">";

    /// <summary>
    /// True for a value type.
    /// </summary>
    public bool IsValueType => Kind is TypeKind.Struct or TypeKind.Enum;

    /// <summary>
    /// The constructors.
    /// </summary>
    public IEnumerable<MethodDeclaration> Constructors => Methods.Where(m => m.IsConstructor);

    /// <summary>
    /// The type initializer, or null.
    /// </summary>
    public MethodDeclaration? TypeInitializer => Methods.FirstOrDefault(m => m.IsTypeInitializer);

    /// <summary>
    /// This type and every type nested in it, outermost first.
    /// </summary>
    public IEnumerable<TypeDeclaration> Family
    {
        get
        {
            yield return this;
            foreach (var nested in NestedTypes)
            {
                foreach (var member in nested.Family)
                {
                    yield return member;
                }
            }
        }
    }
}
