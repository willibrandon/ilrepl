namespace IlRepl.Engine.Binding;

/// <summary>
/// Exposes a type's declared and provisional members before runtime creation.
/// </summary>
/// <remarks>
/// The members a type being written has declared so far, as symbols. A builder cannot describe
/// itself before its type is created, so a reference to a member of an open type resolves through
/// the declarations, whichever scope is asking.
/// </remarks>
public interface IDeclarationMembers
{
    /// <summary>
    /// The definition the members belong to.
    /// </summary>
    TypeSymbol Declaring { get; }

    /// <summary>
    /// The base type as declared, or null for an interface.
    /// </summary>
    TypeSymbol? BaseType { get; }

    /// <summary>
    /// The interfaces the type declares.
    /// </summary>
    IReadOnlyList<TypeSymbol> Interfaces { get; }

    /// <summary>
    /// The fields declared so far.
    /// </summary>
    IReadOnlyList<FieldSymbol> Fields { get; }

    /// <summary>
    /// Finds a field by name.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The field, or null.</returns>
    FieldSymbol? FindField(string name);

    /// <summary>
    /// Lists declared methods and forward references whose headers have not yet been accepted.
    /// </summary>
    /// <remarks>
    /// The methods declared so far, and the ones referenced before their declaration, which
    /// answer false to <see cref="MethodSymbol.IsDeclared"/>.
    /// </remarks>
    IReadOnlyList<MethodSymbol> Methods { get; }

    /// <summary>
    /// Finds the methods with a name.
    /// </summary>
    /// <param name="name">The method name.</param>
    /// <returns>The candidates.</returns>
    IEnumerable<MethodSymbol> FindMethods(string name);

    /// <summary>
    /// True when the type can take a reference to a member declared later.
    /// </summary>
    bool CanDefineForward { get; }

    /// <summary>
    /// Records a member referenced before its declaration, taking its signature at its word.
    /// </summary>
    /// <param name="signature">The signature as the reference spells it, with no identity yet.</param>
    /// <returns>The member with its identity.</returns>
    MethodSymbol DefineForward(MethodSymbol signature);
}
