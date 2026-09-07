namespace IlRepl.Engine;

/// <summary>
/// A type family accepted by the session: the outermost declaration with its nested types, and
/// the runtime type it became once compiled. Nothing is parsed again after acceptance.
/// </summary>
/// <param name="Declaration">The outermost declaration.</param>
/// <param name="Types">Every type of the family by its ILAsm path: the loaded runtime types once compiled, the prototypes until then.</param>
/// <param name="RuntimeType">The loaded runtime type of the outermost declaration, or null while the family is only declared.</param>
/// <param name="Definition">The loaded session assembly, or null while the family is only declared.</param>
/// <param name="Prototypes">The prototype builder and members of every declaration by path; the bodies are bound to these, and an export maps them onto what it writes.</param>
public sealed record SessionType(TypeDeclaration Declaration, IReadOnlyDictionary<string, Type> Types, Type? RuntimeType, DefinitionAssembly? Definition, IReadOnlyDictionary<string, (System.Reflection.Emit.TypeBuilder Prototype, OwnMembers Members)> Prototypes)
{
    /// <summary>
    /// The ILAsm path of the outermost type.
    /// </summary>
    public string FullName => Declaration.FullName;

    /// <summary>
    /// The submission that accepted this family, which orders a rebuild after its dependencies.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Finds the declaration of a type of this family.
    /// </summary>
    /// <param name="type">A prototype or runtime type.</param>
    /// <returns>The declaration, or null when the type is not part of the family.</returns>
    public TypeDeclaration? DeclarationOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        foreach (var (path, member) in Types)
        {
            if (ReferenceEquals(member, type))
            {
                return Declaration.Family.FirstOrDefault(d => d.FullName == path);
            }
        }

        return null;
    }
}
