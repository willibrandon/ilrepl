namespace IlRepl.Engine;

/// <summary>
/// Records an allocated object's exact type without constructing it during edit discovery.
/// </summary>
/// <param name="Type">The type named by the allocation.</param>
/// <param name="WrappedType">The contained type when the instance is an activation handle.</param>
internal sealed record ReflectedInstance(Type Type, Type? WrappedType = null);
