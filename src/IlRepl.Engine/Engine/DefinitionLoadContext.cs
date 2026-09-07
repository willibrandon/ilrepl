using System.Reflection;
using System.Runtime.Loader;

namespace IlRepl.Engine;

/// <summary>
/// The load context of one session assembly. A reference to another session assembly resolves
/// through the registry by exact name; every other reference falls through to the default context,
/// so framework and <c>.load</c>ed assemblies bind as usual.
/// </summary>
/// <remarks>
/// Session assemblies have unique simple names because a context binds a reference by simple
/// name against what it already holds before asking <see cref="Load"/>. The context holds no
/// managed reference into its own assembly: while it is alive and not unloading it keeps the
/// assembly loaded, so the session initiates <see cref="AssemblyLoadContext.Unload"/> when it
/// drops a definition, and collection then follows the objects that still reference it.
/// </remarks>
public sealed class DefinitionLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Initializes a context, collectible where the runtime supports unloading.
    /// </summary>
    /// <param name="name">The name of the assembly the context will hold.</param>
    public DefinitionLoadContext(string name) : base(name, isCollectible: !OperatingSystem.IsBrowser())
    {
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        return SessionAssemblies.IsSessionName(assemblyName.Name) ? SessionAssemblies.Resolve(assemblyName) : null;
    }
}
