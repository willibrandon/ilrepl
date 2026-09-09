using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// A member of a loaded generic definition mapped through a construction during runtime emission.
/// </summary>
/// <param name="Method">The method or constructor on the definition.</param>
internal sealed record RuntimeDefinitionMember(MethodBase Method);
