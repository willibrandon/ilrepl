using System.Reflection;
using System.Runtime.Loader;

namespace IlRepl.Tests.Engine.Binding;

/// <summary>
/// Supplies a custom binding that a metadata snapshot must not invoke or infer from the default context.
/// </summary>
internal sealed class BindingProbeContext(string dependencyName, Assembly dependency) : AssemblyLoadContext(isCollectible: true)
{
    private readonly string _dependencyName = dependencyName;
    private readonly Assembly _dependency = dependency;
    private int _calls;

    /// <summary>
    /// Counts invocations of the user-supplied binding policy.
    /// </summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        Interlocked.Increment(ref _calls);
        return assemblyName.Name == _dependencyName ? _dependency : null;
    }
}
