namespace IlRepl.Engine;

/// <summary>
/// Retains a delegate factory's proven target name when its receiver is supplied at runtime.
/// </summary>
/// <param name="Name">The method name enforced by the runtime delegate factory.</param>
internal sealed record ReflectedDelegateName(string Name);
