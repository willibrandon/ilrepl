namespace IlRepl.Engine.Binding;

/// <summary>
/// Represents a bound method or constructor, its retained declaration, and optional call-site parameter types.
/// </summary>
/// <remarks>
/// A member reference bound to a method or constructor: the member as the reference sees it, the
/// declaration it came from when the type is being written, and the vararg call-site types.
/// </remarks>
/// <param name="Method">The member, with the declaring construction's and the instantiation's arguments substituted.</param>
/// <param name="Definition">The declaration as written, before substitution, for a member of a type being written; null otherwise.</param>
/// <param name="OptionalParameterTypes">The types after <c>...</c> in a vararg call site, or null when the call is not vararg.</param>
public sealed record BoundMethod(MethodSymbol Method, MethodSymbol? Definition, IReadOnlyList<TypeSymbol>? OptionalParameterTypes);
