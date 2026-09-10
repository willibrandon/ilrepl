using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Describes an indexed type's identity, names, arity, and presentation facts.
/// </summary>
/// <remarks>
/// One type definition as an index lists it: its identity, its names in every form a reference
/// can take, its arity, and the facts ranking and presentation need. The symbol with its full
/// facts comes from the definition's source.
/// </remarks>
/// <param name="Definition">The identity.</param>
/// <param name="Name">The metadata name, arity suffix included.</param>
/// <param name="Namespace">The namespace of the outermost type, or empty.</param>
/// <param name="IlPath">The ILAsm path: <c>Namespace.Outer/Inner</c>, arity suffixes included.</param>
/// <param name="Attributes">The type attributes.</param>
/// <param name="Arity">The number of generic parameters, inherited ones included.</param>
/// <param name="Kind">The kind.</param>
/// <param name="IsCompilerGenerated">True for a compiler-generated type.</param>
public sealed record TypeIndexEntry(DefinitionId Definition, string Name, string Namespace, string IlPath, TypeAttributes Attributes,
    int Arity, TypeIndexKind Kind, bool IsCompilerGenerated)
{
    /// <summary>
    /// The name without its arity suffix.
    /// </summary>
    public string BareName
    {
        get
        {
            var tick = Name.LastIndexOf('`');
            return tick > 0 ? Name[..tick] : Name;
        }
    }

    /// <summary>
    /// True when the type is nested in another.
    /// </summary>
    public bool IsNested => IlPath.Contains('/');

    /// <summary>
    /// True when the type and every type enclosing it are public: what an assembly exports.
    /// </summary>
    public bool IsVisible { get; init; }

    /// <summary>
    /// The simple name of the defining assembly; empty for a type being written.
    /// </summary>
    public string AssemblyName { get; init; } = "";

    /// <summary>
    /// True for a type the session declared, written or accepted.
    /// </summary>
    public bool IsSession { get; init; }
}
