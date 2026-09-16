using System.Reflection;
using System.Runtime.Loader;

namespace IlRepl.Engine;

/// <summary>
/// Resolves one generated definition's references through the session registry and its captured owned dependency context.
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
    private readonly ReferenceLoadContext? _references = ReferenceLoadScope.Current;
    /// <summary>
    /// Initializes a context, collectible where the runtime supports unloading.
    /// </summary>
    /// <param name="name">The name of the assembly the context will hold.</param>
    public DefinitionLoadContext(string name) : base(name, isCollectible: !OperatingSystem.IsBrowser())
    {
    }

    /// <summary>
    /// Copies an existing external dependency binding without triggering runtime resolution.
    /// </summary>
    /// <param name="name">The requested assembly identity.</param>
    /// <returns>The loaded owned dependency, or null.</returns>
    internal Assembly? FindLoadedReference(AssemblyName name) => _references?.FindLoaded(name);

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        return SessionAssemblies.IsSessionName(assemblyName.Name)
            ? SessionAssemblies.Resolve(assemblyName) : _references?.Resolve(assemblyName);
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName) => _references?.ResolveNative(unmanagedDllName) ?? 0;
}
