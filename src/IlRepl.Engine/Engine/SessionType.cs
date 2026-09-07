namespace IlRepl.Engine;

/// <summary>
/// A type family accepted by the session: the outermost declaration with its nested types, and
/// the runtime type it became once compiled. Nothing is parsed again after acceptance.
/// </summary>
/// <param name="Declaration">The outermost declaration.</param>
/// <param name="RuntimeType">The loaded runtime type of the outermost declaration, or null while the family is only declared.</param>
public sealed record SessionType(TypeDeclaration Declaration, Type? RuntimeType)
{
    /// <summary>
    /// The ILAsm path of the outermost type.
    /// </summary>
    public string FullName => Declaration.FullName;
}
