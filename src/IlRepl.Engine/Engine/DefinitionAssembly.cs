using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A session-owned assembly and the session assemblies it references. The references are strong
/// on purpose: the runtime binds an assembly reference the first time it is used, so a retained
/// instance, delegate, or type must keep everything its assembly may still need to load alive
/// until it is released. The registry keeps this record alive exactly as long as the assembly.
/// </summary>
public sealed class DefinitionAssembly
{
    internal DefinitionAssembly(Assembly assembly, SessionAssemblyKind kind, IReadOnlyList<DefinitionAssembly> dependencies, DefinitionLoadContext? context)
    {
        Assembly = assembly;
        Kind = kind;
        Dependencies = dependencies;
        Context = context;
    }

    /// <summary>
    /// The loaded assembly.
    /// </summary>
    public Assembly Assembly { get; }

    /// <summary>
    /// What the assembly holds.
    /// </summary>
    public SessionAssemblyKind Kind { get; }

    /// <summary>
    /// The session assemblies this one references, held strongly.
    /// </summary>
    public IReadOnlyList<DefinitionAssembly> Dependencies { get; }

    /// <summary>
    /// The load context that holds the assembly, or null for a cell built with Reflection.Emit.
    /// </summary>
    public DefinitionLoadContext? Context { get; }

    /// <summary>
    /// The simple name.
    /// </summary>
    public string Name => Assembly.GetName().Name ?? "";
}
