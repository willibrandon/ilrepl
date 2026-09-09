using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Keeps an assembly alive while its raw metadata is read. The reader over a loaded assembly's
/// metadata section is valid only while the assembly stays loaded; a lease is the strong
/// reference that guarantees it, and a snapshot holds one per assembly it reads for as long as
/// the snapshot lives.
/// </summary>
public sealed class MetadataLease : IDisposable
{
    private Assembly? _assembly;

    internal MetadataLease(Assembly assembly, AssemblySymbolSource source)
    {
        _assembly = assembly;
        Source = source;
    }

    /// <summary>
    /// The symbol source the lease keeps readable.
    /// </summary>
    public AssemblySymbolSource Source { get; }

    /// <summary>
    /// The assembly held.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The lease was released.</exception>
    public Assembly Assembly => _assembly ?? throw new ObjectDisposedException(nameof(MetadataLease));

    /// <summary>
    /// Releases the hold on the assembly.
    /// </summary>
    public void Dispose() => _assembly = null;
}
