using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Indexes session and loaded metadata types for completion and typo suggestions.
/// </summary>
/// <remarks>
/// Every type a snapshot can name: the session's own, then the definitions of each loaded
/// assembly in the resolver's search order, from metadata alone. The palette and the did-you-mean
/// read one index, so a name the palette offers is a name a suggestion can make and both spell it
/// the same way.
/// </remarks>
public sealed class TypeIndex
{
    private readonly BindingSnapshot _snapshot;
    private readonly SnapshotBindingScope _scope;
    private readonly List<TypeIndexEntry> _entries = [];
    private readonly Dictionary<TypeIndexEntry, TypeSymbol> _sessionSymbols = [];
    private readonly Dictionary<string, List<TypeIndexEntry>> _byFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TypeIndexEntry>> _bySimpleName = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds the index of a snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    public TypeIndex(BindingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _scope = new SnapshotBindingScope(snapshot);
        foreach (var (fullName, _, symbol) in snapshot.Types.Entries)
        {
            var definition = symbol.DefinitionOrSelf;
            var entry = new TypeIndexEntry(definition.Definition, definition.Name, definition.Namespace, fullName, definition.Attributes,
                definition.GenericParameterNames.Count, KindOf(definition), definition.Name.StartsWith('<'))
            {
                IsVisible = true,
                IsSession = true,
                AssemblyName = definition.AssemblyName,
            };
            Add(entry);
            _sessionSymbols[entry] = symbol;
        }

        foreach (var source in snapshot.SearchOrder)
        {
            foreach (var entry in source.Index.Entries)
            {
                Add(entry with { AssemblyName = source.Name });
            }
        }
    }

    private void Add(TypeIndexEntry entry)
    {
        _entries.Add(entry);
        Index(_byFullName, entry.IlPath, entry);
        Index(_bySimpleName, entry.Name, entry);
    }

    private static void Index(Dictionary<string, List<TypeIndexEntry>> table, string key, TypeIndexEntry entry)
    {
        if (!table.TryGetValue(key, out var list))
        {
            list = [];
            table[key] = list;
        }

        list.Add(entry);
    }

    private static TypeIndexKind KindOf(TypeSymbol definition)
    {
        if (definition.Kind == TypeSymbolKind.Primitive)
        {
            return TypeIndexKind.Primitive;
        }

        if (definition.IsInterface)
        {
            return TypeIndexKind.Interface;
        }

        return definition.IsValueType ? TypeIndexKind.Struct : TypeIndexKind.Class;
    }

    /// <summary>
    /// Every entry: the session's types first, then each assembly's in search order.
    /// </summary>
    public IReadOnlyList<TypeIndexEntry> Entries => _entries;

    /// <summary>
    /// The scope the index binds spellings in.
    /// </summary>
    public IBindingScope Scope => _scope;

    /// <summary>
    /// The entries with an ILAsm path.
    /// </summary>
    /// <param name="ilPath">The path: <c>Namespace.Outer/Inner</c>.</param>
    /// <returns>The entries, in index order.</returns>
    public IReadOnlyList<TypeIndexEntry> ByFullName(string ilPath)
    {
        ArgumentNullException.ThrowIfNull(ilPath);
        return _byFullName.TryGetValue(ilPath, out var list) ? list : [];
    }

    /// <summary>
    /// The entries with a metadata name, arity suffix included.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The entries, in index order.</returns>
    public IReadOnlyList<TypeIndexEntry> BySimpleName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _bySimpleName.TryGetValue(name, out var list) ? list : [];
    }

    /// <summary>
    /// Finds type entries matching an optional assembly hint, namespace, and nested path.
    /// </summary>
    /// <remarks>
    /// The entries whose path matches an assembly hint, a namespace, and a nesting path, each
    /// compared case-insensitively when given.
    /// </remarks>
    /// <param name="assemblyHint">The assembly, or null for any.</param>
    /// <param name="ns">The namespace, or null for any.</param>
    /// <param name="nesting">The nesting path with the type's own name last, or null for any.</param>
    /// <returns>The entries, in index order.</returns>
    public IReadOnlyList<TypeIndexEntry> ByPath(string? assemblyHint, string? ns, string? nesting)
    {
        return [.. _entries.Where(e =>
            (assemblyHint is null || string.Equals(e.AssemblyName, assemblyHint, StringComparison.OrdinalIgnoreCase))
            && (ns is null || string.Equals(e.Namespace, ns, StringComparison.OrdinalIgnoreCase))
            && (nesting is null || string.Equals(NestingOf(e), nesting, StringComparison.OrdinalIgnoreCase)))];
    }

    private static string NestingOf(TypeIndexEntry entry) => entry.Namespace.Length == 0 || !entry.IlPath.StartsWith(entry.Namespace + ".",
        StringComparison.Ordinal) ? entry.IlPath : entry.IlPath[(entry.Namespace.Length + 1)..];

    /// <summary>
    /// Finds the type selected by a bare short name, or null for missing or ambiguous names.
    /// </summary>
    /// <remarks>
    /// The type a bare short name binds to under the resolver's rules, or null when it binds to
    /// nothing or is ambiguous.
    /// </remarks>
    /// <param name="name">The short name, arity suffix included.</param>
    /// <returns>The type, or null.</returns>
    public TypeSymbol? ShortNameTarget(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            return _scope.LookupType(name, null, 0, false).Type;
        }
        catch (ReplException)
        {
            return null;
        }
    }

    /// <summary>
    /// The symbol of an entry, with its full facts.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The symbol, or null when its assembly is gone.</returns>
    public TypeSymbol? SymbolOf(TypeIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_sessionSymbols.TryGetValue(entry, out var session))
        {
            return session;
        }

        var source = _snapshot.Catalog.Source(entry.Definition.Assembly);
        var handle = source?.TypeHandleOf(entry.Definition);
        return source is not null && handle is { } h ? source.Definition(h) : null;
    }
}
