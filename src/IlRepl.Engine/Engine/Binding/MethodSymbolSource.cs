namespace IlRepl.Engine.Binding;

/// <summary>
/// Where a <see cref="MethodSymbol"/> or <see cref="FieldSymbol"/> comes from, which decides how a
/// reference to it is emitted.
/// </summary>
public enum MethodSymbolSource
{
    /// <summary>
    /// A member of a loaded assembly, framework or session.
    /// </summary>
    Loaded,

    /// <summary>
    /// A method defined with <c>.method</c> at the top level of the session, bound to its trampoline when emitted.
    /// </summary>
    Session,

    /// <summary>
    /// A member of a type the session is writing, declared by its header.
    /// </summary>
    Declared,

    /// <summary>
    /// A member of a type the session is writing, referenced before its declaration.
    /// </summary>
    Forward,
}
