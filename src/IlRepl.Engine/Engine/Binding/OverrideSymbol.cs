namespace IlRepl.Engine.Binding;

/// <summary>
/// An explicit mapping between a virtual slot and the method that implements it.
/// </summary>
/// <param name="Target">The resolved virtual slot.</param>
/// <param name="Body">The implementing method's signature.</param>
internal sealed record OverrideSymbol(BoundMethod Target, MethodSymbol Body);
