using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Indexes an assembly's type definitions and forwarded aliases without loading dependencies.
/// </summary>
/// <remarks>
/// The type definitions of one loaded assembly and the types it exports from elsewhere, read from
/// its metadata once: every definition by its qualified name, nested definitions by their parent,
/// and the forwarders that make a reference through a facade land on the assembly that defines the
/// type. Nothing is loaded to build it.
/// </remarks>
public sealed class AssemblyTypeIndex
{
    private readonly AssemblySymbolSource _source;
    private readonly Lock _entryGate = new();
    private readonly Dictionary<(string Namespace, string Name), TypeDefinitionHandle> _topLevel = [];
    private readonly Dictionary<TypeDefinitionHandle, Dictionary<string, TypeDefinitionHandle>> _nested = [];
    private readonly Dictionary<(string Namespace, string Name), AssemblyReferenceHandle> _forwarders = [];
    private readonly Dictionary<(string Namespace, string Name), ExportedTypeHandle> _exportedTopLevel = [];
    private readonly Dictionary<ExportedTypeHandle, Dictionary<string, ExportedTypeHandle>> _exportedNested = [];
    private readonly Dictionary<string, List<TypeDefinitionHandle>> _bySimpleName = new(StringComparer.Ordinal);
    private IReadOnlyList<TypeIndexEntry>? _entries;

    internal AssemblyTypeIndex(AssemblySymbolSource source, MetadataReader reader)
        : this(source)
    {
        foreach (var _ in BuildSteps(reader))
        {
        }
    }

    private AssemblyTypeIndex(AssemblySymbolSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Builds the same metadata index cooperatively before publishing it to other readers.
    /// </summary>
    /// <param name="source">The leased assembly source.</param>
    /// <param name="reader">Its metadata reader.</param>
    /// <param name="cancellationToken">Cancels construction between metadata rows.</param>
    /// <returns>The complete immutable index.</returns>
    internal static async ValueTask<AssemblyTypeIndex> CreateAsync(
        AssemblySymbolSource source, MetadataReader reader, CancellationToken cancellationToken)
    {
        var index = new AssemblyTypeIndex(source);
        var processed = 0;
        var slice = Stopwatch.GetTimestamp();
        foreach (var _ in index.BuildSteps(reader))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++processed % 128 == 0 && Stopwatch.GetElapsedTime(slice) >= TimeSpan.FromMilliseconds(4))
            {
                // Browser continuations incur a timer turn; yield for elapsed work, not every small batch of rows.
                await Task.Yield();
                slice = Stopwatch.GetTimestamp();
            }
        }

        return index;
    }

    private IEnumerable<byte> BuildSteps(MetadataReader reader)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            try
            {
                var definition = reader.GetTypeDefinition(handle);
                var name = reader.GetString(definition.Name);
                var declaring = definition.GetDeclaringType();
                if (declaring.IsNil)
                {
                    if (name == "<Module>")
                    {
                        continue;
                    }

                    _topLevel[(reader.GetString(definition.Namespace), name)] = handle;
                }
                else
                {
                    if (!_nested.TryGetValue(declaring, out var children))
                    {
                        children = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
                        _nested[declaring] = children;
                    }

                    children[name] = handle;
                }

                if (!_bySimpleName.TryGetValue(name, out var same))
                {
                    same = [];
                    _bySimpleName[name] = same;
                }

                same.Add(handle);
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // An unreadable type cannot hide the healthy definitions beside it.
            }

            yield return 0;
        }

        foreach (var handle in reader.ExportedTypes)
        {
            try
            {
                var exported = reader.GetExportedType(handle);
                var name = reader.GetString(exported.Name);
                switch (exported.Implementation.Kind)
                {
                    case HandleKind.AssemblyReference:
                        _forwarders[(reader.GetString(exported.Namespace), name)] = (AssemblyReferenceHandle)exported.Implementation;
                        _exportedTopLevel[(reader.GetString(exported.Namespace), name)] = handle;
                        break;
                    case HandleKind.ExportedType:
                    {
                        var parent = (ExportedTypeHandle)exported.Implementation;
                        if (!_exportedNested.TryGetValue(parent, out var children))
                        {
                            children = new Dictionary<string, ExportedTypeHandle>(StringComparer.Ordinal);
                            _exportedNested[parent] = children;
                        }

                        children[name] = handle;
                        break;
                    }

                    default:
                        break;
                }

            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // An unreadable type cannot hide the healthy definitions beside it.
            }

            yield return 0;
        }
    }

    /// <summary>
    /// Every type definition of the assembly, materialized on demand with the module pseudo-type excluded.
    /// </summary>
    public IReadOnlyList<TypeIndexEntry> Entries
    {
        get
        {
            lock (_entryGate)
            {
                return _entries ??= [.. ReadEntries()];
            }
        }
    }

    /// <summary>
    /// Materializes completion details cooperatively without making known-name lookups wait for every type's facts.
    /// </summary>
    /// <param name="cancellationToken">Cancels unpublished work between definitions.</param>
    /// <returns>A task that completes once the shared entries are available.</returns>
    public async ValueTask WarmEntriesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_entryGate)
        {
            if (_entries is not null)
            {
                return;
            }
        }

        using var lease = _source.Lease();
        var entries = new List<TypeIndexEntry>();
        var processed = 0;
        var slice = Stopwatch.GetTimestamp();
        foreach (var handle in _source.Reader.TypeDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EntryOf(handle) is { } entry)
            {
                entries.Add(entry);
            }
            if (++processed % 128 == 0 && Stopwatch.GetElapsedTime(slice) >= TimeSpan.FromMilliseconds(4))
            {
                await Task.Yield();
                slice = Stopwatch.GetTimestamp();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_entryGate)
        {
            _entries ??= entries;
        }
    }

    private IEnumerable<TypeIndexEntry> ReadEntries()
    {
        foreach (var handle in _source.Reader.TypeDefinitions)
        {
            if (EntryOf(handle) is { } entry)
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// The entry for a definition.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <returns>The entry, or null for the module pseudo-type.</returns>
    public TypeIndexEntry? EntryOf(TypeDefinitionHandle handle)
    {
        try
        {
            var definition = _source.Reader.GetTypeDefinition(handle);
            return definition.GetDeclaringType().IsNil && _source.Reader.GetString(definition.Name) == "<Module>"
                ? null : _source.Entry(handle);
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Finds a top-level definition by namespace and name.
    /// </summary>
    /// <param name="ns">The namespace, or empty.</param>
    /// <param name="name">The metadata name, arity suffix included.</param>
    /// <param name="handle">The definition.</param>
    /// <returns>True when the assembly defines it.</returns>
    public bool TryGetDefinition(string ns, string name, out TypeDefinitionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(name);
        return _topLevel.TryGetValue((ns, name), out handle);
    }

    /// <summary>
    /// Finds a definition nested in another.
    /// </summary>
    /// <param name="parent">The enclosing definition.</param>
    /// <param name="name">The nested type's metadata name.</param>
    /// <param name="handle">The nested definition.</param>
    /// <returns>True when the parent declares it.</returns>
    public bool TryGetNested(TypeDefinitionHandle parent, string name, out TypeDefinitionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(name);
        handle = default;
        return _nested.TryGetValue(parent, out var children) && children.TryGetValue(name, out handle);
    }

    /// <summary>
    /// The definitions nested in another, in declaration order.
    /// </summary>
    /// <param name="parent">The enclosing definition.</param>
    /// <returns>The nested definitions.</returns>
    public IReadOnlyList<TypeDefinitionHandle> NestedOf(TypeDefinitionHandle parent) => _nested.TryGetValue(parent,
        out var children) ? [.. children.Values] : [];

    /// <summary>
    /// Finds the assembly a top-level type is forwarded to.
    /// </summary>
    /// <param name="ns">The namespace, or empty.</param>
    /// <param name="name">The metadata name.</param>
    /// <param name="target">The assembly reference the forwarder names.</param>
    /// <returns>True when the assembly forwards the type.</returns>
    public bool TryGetForwarder(string ns, string name, out AssemblyReferenceHandle target)
    {
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(name);
        return _forwarders.TryGetValue((ns, name), out target);
    }

    /// <summary>
    /// Finds the exported-type row of a forwarded top-level type, the parent of nested exported types.
    /// </summary>
    /// <param name="ns">The namespace, or empty.</param>
    /// <param name="name">The metadata name.</param>
    /// <param name="handle">The row.</param>
    /// <returns>True when the assembly exports the type.</returns>
    public bool TryGetExported(string ns, string name, out ExportedTypeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(name);
        return _exportedTopLevel.TryGetValue((ns, name), out handle);
    }

    /// <summary>
    /// Finds a nested type exported under an exported parent.
    /// </summary>
    /// <param name="parent">The parent's row.</param>
    /// <param name="name">The nested type's name.</param>
    /// <param name="handle">The nested row.</param>
    /// <returns>True when the parent exports it.</returns>
    public bool TryGetExportedNested(ExportedTypeHandle parent, string name, out ExportedTypeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(name);
        handle = default;
        return _exportedNested.TryGetValue(parent, out var children) && children.TryGetValue(name, out handle);
    }

    /// <summary>
    /// The definitions with a metadata name, nested ones included.
    /// </summary>
    /// <param name="name">The metadata name, arity suffix included.</param>
    /// <returns>The definitions, in metadata order.</returns>
    public IReadOnlyList<TypeDefinitionHandle> Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _bySimpleName.TryGetValue(name, out var same) ? same : [];
    }

    /// <summary>
    /// The publicly visible definitions with this metadata name, including visible nested types.
    /// </summary>
    /// <param name="name">The metadata name.</param>
    /// <returns>The visible definitions.</returns>
    public IEnumerable<TypeDefinitionHandle> VisibleNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var handle in Named(name))
        {
            if (EntryOf(handle) is { IsVisible: true })
            {
                yield return handle;
            }
        }
    }

    /// <summary>
    /// The token of a definition, for a <see cref="DefinitionId"/>.
    /// </summary>
    /// <param name="handle">The definition.</param>
    /// <returns>The token.</returns>
    public static int TokenOf(TypeDefinitionHandle handle) => MetadataTokens.GetToken(handle);
}
