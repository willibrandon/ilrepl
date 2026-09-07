using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// One compiled body of a session method: its assembly, the method itself, and the delegate its
/// trampoline is bound through. The delegate is what keeps the assembly alive.
/// </summary>
/// <param name="Definition">The version's assembly.</param>
/// <param name="Body">The compiled method on the version's <c>IlRepl.Cell</c>.</param>
/// <param name="Implementation">A delegate of the trampoline's delegate type over <paramref name="Body"/>.</param>
public sealed record CompiledMethodVersion(DefinitionAssembly Definition, MethodInfo Body, Delegate Implementation);
