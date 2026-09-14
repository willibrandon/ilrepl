using System.Reflection;
using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// An <c>.override</c> line: the method whose slot the declaring method implements.
/// </summary>
/// <param name="Target">The base or interface method being implemented.</param>
/// <param name="TargetDescription">The target as a listing shows it.</param>
/// <param name="Source">The line as typed.</param>
public sealed record OverrideDeclaration(MethodBase Target, string TargetDescription, string Source)
{
    /// <summary>
    /// The bound target identity, including targets represented by wrappers without a runtime metadata token.
    /// </summary>
    internal DefinitionId? TargetDefinition { get; init; }
}
