namespace IlRepl.Engine;

/// <summary>
/// A type family accepted by the session: the outermost declaration with its nested types, and
/// the runtime type it became once compiled. Nothing is parsed again after acceptance.
/// </summary>
/// <param name="Declaration">The outermost declaration.</param>
/// <param name="Types">Every type of the family by its ILAsm path: the loaded runtime types once compiled, the prototypes until then.</param>
/// <param name="RuntimeType">The loaded runtime type of the outermost declaration, or null while the family is only declared.</param>
public sealed record SessionType(TypeDeclaration Declaration, IReadOnlyDictionary<string, Type> Types, Type? RuntimeType)
{
    /// <summary>
    /// The ILAsm path of the outermost type.
    /// </summary>
    public string FullName => Declaration.FullName;

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
