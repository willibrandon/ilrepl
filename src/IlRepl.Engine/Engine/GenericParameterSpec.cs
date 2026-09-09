using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Holds a generic parameter's name, variance and unbound constraint syntax.
/// </summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Attributes">Variance and the special constraints.</param>
/// <param name="ConstraintTexts">The constraint types as written.</param>
public sealed record GenericParameterSpec(string Name, GenericParameterAttributes Attributes, IReadOnlyList<string> ConstraintTexts);
