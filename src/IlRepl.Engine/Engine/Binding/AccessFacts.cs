namespace IlRepl.Engine.Binding;

/// <summary>
/// Supplies type relationships, session ownership, and names for accessibility checks.
/// </summary>
/// <remarks>
/// What an accessibility verdict needs to know about types, supplied by whichever scope is asking:
/// the base chain, which types the session declared, and how a type is spelled in a message.
/// </remarks>
/// <param name="BaseOf">The base type of a type, or null.</param>
/// <param name="IsSessionType">True for a type the session declared.</param>
/// <param name="Pretty">Spells a type for a message.</param>
public sealed record AccessFacts(Func<TypeSymbol, TypeSymbol?> BaseOf, Func<TypeSymbol, bool> IsSessionType, Func<TypeSymbol?,
    string> Pretty)
{
    /// <summary>
    /// The facts a binding scope provides.
    /// </summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The facts.</returns>
    public static AccessFacts From(IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new AccessFacts(scope.BaseOf, scope.IsSessionType, scope.Pretty);
    }
}
