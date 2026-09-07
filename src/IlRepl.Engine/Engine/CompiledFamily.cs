namespace IlRepl.Engine;

/// <summary>
/// A type family written and loaded: its session assembly and the runtime type of every
/// declaration in it, by ILAsm path.
/// </summary>
/// <param name="Definition">The loaded session assembly.</param>
/// <param name="Types">The runtime types by path.</param>
public sealed record CompiledFamily(DefinitionAssembly Definition, IReadOnlyDictionary<string, Type> Types);
