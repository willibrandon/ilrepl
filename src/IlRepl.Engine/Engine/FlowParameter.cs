namespace IlRepl.Engine;

/// <summary>
/// Retains the constraints that distinguish an unboxed generic value from its boxed reference.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
/// <param name="IsReference">Whether the parameter is known to be a reference type.</param>
/// <param name="IsValueType">Whether the parameter has the non-nullable value-type constraint.</param>
/// <param name="Constraints">The declared base and interface constraints.</param>
internal sealed record FlowParameter<T>(bool IsReference, bool IsValueType, IReadOnlyList<T> Constraints) where T : class;
