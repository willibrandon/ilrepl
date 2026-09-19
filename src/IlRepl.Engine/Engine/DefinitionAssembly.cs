using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// A session-owned assembly and the session assemblies it references.
/// </summary>
/// <remarks>
/// The references are strong on purpose: the runtime binds an assembly reference the first time it is used, so a retained instance,
/// delegate, or type must keep everything its assembly may still need to load alive until it is released. The registry keeps this record
/// alive exactly as long as the assembly.
/// </remarks>
public sealed class DefinitionAssembly
{
    /// <summary>
    /// Initializes the record of a loaded session assembly.
    /// </summary>
    /// <param name="assembly">The loaded assembly.</param>
    /// <param name="kind">What the assembly holds.</param>
    /// <param name="dependencies">The session assemblies this one references, copied and held strongly.</param>
    /// <param name="context">The load context that holds the assembly, or null for a cell built with Reflection.Emit.</param>
    internal DefinitionAssembly(
        Assembly assembly,
        SessionAssemblyKind kind,
        IReadOnlyList<DefinitionAssembly> dependencies,
        DefinitionLoadContext? context)
    {
        Assembly = assembly;
        Kind = kind;
        _dependencies = [.. dependencies];
        Context = context;
    }

    private readonly List<DefinitionAssembly> _dependencies;

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
    public IReadOnlyList<DefinitionAssembly> Dependencies => _dependencies;

    /// <summary>
    /// Adds a dependency loaded after this assembly.
    /// </summary>
    /// <remarks>
    /// This happens when definitions that refer to each other are written together and loaded one after another.
    /// </remarks>
    /// <param name="dependency">The session assembly this one references.</param>
    public void AddDependency(DefinitionAssembly dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        if (!ReferenceEquals(dependency, this) && !_dependencies.Contains(dependency))
        {
            _dependencies.Add(dependency);
        }
    }

    /// <summary>
    /// The load context that holds the assembly, or null for a cell built with Reflection.Emit.
    /// </summary>
    public DefinitionLoadContext? Context { get; }

    /// <summary>
    /// The PE image the assembly was loaded from, or null for a cell.
    /// </summary>
    /// <remarks>
    /// The record lives exactly as long as the assembly, so keeping the bytes here adds no root; they let a listing read the body straight
    /// from the image that was loaded.
    /// </remarks>
    public byte[]? Image { get; init; }

    /// <summary>
    /// The simple name.
    /// </summary>
    public string Name => Assembly.GetName().Name ?? "";
}
