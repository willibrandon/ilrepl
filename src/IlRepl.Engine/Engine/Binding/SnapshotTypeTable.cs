namespace IlRepl.Engine.Binding;

/// <summary>
/// Captures session type paths and lookup rules with independently owned provisional nested types.
/// </summary>
/// <remarks>
/// The session's type table as a snapshot holds it: every path the session can name, with the
/// symbol it means, and the same lookup rules as <see cref="TypeTable"/>. A path into a family
/// being written may name a nested type declared later; the placeholder it gets is the
/// snapshot's own.
/// </remarks>
public sealed class SnapshotTypeTable
{
    private readonly List<(string FullName, string ShortName, TypeSymbol Type)> _entries = [];
    private readonly Dictionary<DefinitionId, DeclarationSymbol> _declarations = [];
    private readonly Dictionary<string, TypeSymbol> _placeholders = new(StringComparer.Ordinal);
    private readonly long _sessionAssembly;

    /// <summary>
    /// Initializes a table for a session.
    /// </summary>
    /// <param name="sessionAssembly">The assembly instance placeholders belong to.</param>
    /// <param name="openFamilyPaths">The paths of the family being written, outermost first, or empty.</param>
    public SnapshotTypeTable(long sessionAssembly, IReadOnlyList<string> openFamilyPaths)
    {
        ArgumentNullException.ThrowIfNull(openFamilyPaths);
        _sessionAssembly = sessionAssembly;
        OpenFamilyPaths = openFamilyPaths;
    }

    /// <summary>
    /// The paths of the blocks being written, innermost last; empty outside a type block.
    /// </summary>
    public IReadOnlyList<string> OpenFamilyPaths { get; }

    /// <summary>
    /// The entries, in the order they were added.
    /// </summary>
    public IReadOnlyList<(string FullName, string ShortName, TypeSymbol Type)> Entries => _entries;

    /// <summary>
    /// The declarations of the types being written, by identity.
    /// </summary>
    public IReadOnlyDictionary<DefinitionId, DeclarationSymbol> Declarations => _declarations;

    /// <summary>
    /// Adds a type under its ILAsm path.
    /// </summary>
    /// <param name="fullName">The path.</param>
    /// <param name="type">The symbol.</param>
    /// <param name="declaration">The declaration when the type is being written, or null.</param>
    public void Add(string fullName, TypeSymbol type, DeclarationSymbol? declaration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentNullException.ThrowIfNull(type);
        var slash = fullName.LastIndexOf('/');
        var dot = fullName.LastIndexOf('.');
        var shortName = fullName[(Math.Max(slash, dot) + 1)..];
        foreach (var previous in _entries.Where(entry => entry.FullName == fullName && entry.Type.Definition != type.Definition))
        {
            _declarations.Remove(previous.Type.Definition);
        }

        _entries.RemoveAll(e => e.FullName == fullName);
        _entries.Add((fullName, shortName, type));
        if (declaration is not null)
        {
            _declarations[type.Definition] = declaration;
        }
    }

    /// <summary>
    /// Copies this table and its declarations into an independent editing checkpoint.
    /// </summary>
    /// <param name="openPaths">The open family paths, or null to keep the current paths.</param>
    /// <returns>An independently mutable table with identical symbol identities.</returns>
    internal SnapshotTypeTable Clone(IReadOnlyList<string>? openPaths = null)
    {
        var clone = new SnapshotTypeTable(_sessionAssembly, openPaths ?? OpenFamilyPaths);
        clone._entries.AddRange(_entries);
        foreach (var (identity, declaration) in _declarations)
        {
            clone._declarations.Add(identity, declaration.Clone());
        }

        foreach (var (path, symbol) in _placeholders)
        {
            clone._placeholders.Add(path, symbol);
        }

        return clone;
    }

    /// <summary>
    /// Removes a family and every nested path before a replacement is added.
    /// </summary>
    /// <param name="path">The outermost IL path.</param>
    internal void RemoveFamily(string path)
    {
        var removed = _entries.Where(e => e.FullName == path || e.FullName.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
        foreach (var entry in removed)
        {
            _entries.Remove(entry);
            _declarations.Remove(entry.Type.Definition);
            _placeholders.Remove(entry.FullName);
        }
    }

    /// <summary>
    /// The declaration of a type being written, or null for a loaded type.
    /// </summary>
    /// <param name="type">The type, its definition, or a construction of it.</param>
    /// <returns>The declaration, or null.</returns>
    public DeclarationSymbol? DeclarationOf(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _declarations.TryGetValue(type.DefinitionOrSelf.Definition, out var declaration) ? declaration : null;
    }

    /// <summary>
    /// Finds a type by the name written in IL, with the rules of <see cref="TypeTable.TryResolve"/>.
    /// </summary>
    /// <param name="name">The name as written, with its arity suffix if any.</param>
    /// <param name="withArguments">True when type arguments follow.</param>
    /// <param name="valueType">True when the reference was written with <c>valuetype</c>.</param>
    /// <param name="type">The type found.</param>
    /// <param name="allowForward">Whether an unresolved nested path may create a placeholder.</param>
    /// <returns>True when a session type matched.</returns>
    /// <exception cref="ReplException">A short name matched more than one type.</exception>
    public bool TryResolve(
        string name, bool withArguments, bool valueType,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TypeSymbol? type, bool allowForward = true)
    {
        ArgumentNullException.ThrowIfNull(name);
        type = null;
        if (_entries.Count == 0)
        {
            return false;
        }

        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].FullName == name || (withArguments && !name.Contains('`') && StripArity(_entries[i].FullName) == name))
            {
                type = _entries[i].Type;
                return true;
            }
        }

        if (name.Contains('.') || name.Contains('/'))
        {
            // Only a path into the family being written may name a type declared later.
            type = allowForward && name.Contains('/') ? Forward(name, valueType) : null;
            return type is not null;
        }

        var matches = _entries.Where(e => e.ShortName == name || (withArguments && StripArity(e.ShortName) == name)).Select(e
            => e.Type).Distinct().ToList();
        if (matches.Count == 1)
        {
            type = matches[0];
            return true;
        }

        if (matches.Count > 1)
        {
            var names = string.Join(", ", _entries.Where(e => matches.Contains(e.Type)).Select(e => e.FullName));
            throw new ReplException($"'{name}' is ambiguous: {names} (write the full name)");
        }

        return false;
    }

    private TypeSymbol? Forward(string name, bool valueType)
    {
        // Outer/Inner names a nested type that may be declared later in the family being written;
        // a placeholder of the referenced kind stands in until then, in this snapshot alone.
        if (OpenFamilyPaths.Count == 0)
        {
            return null;
        }

        var slash = name.LastIndexOf('/');
        var enclosingPath = name[..slash];
        var nested = name[(slash + 1)..];
        if (!OpenFamilyPaths.Contains(enclosingPath) || !InstructionParser.IsIdentifier(nested.Replace("`", "", StringComparison.Ordinal)))
        {
            return null;
        }

        if (_placeholders.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var enclosing = _entries.LastOrDefault(e => e.FullName == enclosingPath).Type;
        var placeholder = TypeSymbol.Named(
            DefinitionId.ForDeclaration(_sessionAssembly, -Interlocked.Increment(ref s_placeholders)),
            nested,
            enclosing?.Namespace ?? "",
            enclosing,
            "",
            System.Reflection.TypeAttributes.NestedPublic | (
                valueType ? System.Reflection.TypeAttributes.Sealed : System.Reflection.TypeAttributes.Class),
            valueType,
            []);
        _placeholders[name] = placeholder;
        _declarations[placeholder.Definition] = new DeclarationSymbol(placeholder, valueType ? TypeSymbol.Primitive(
            "object") : TypeSymbol.Primitive("object"), [], [], [], [], false)
        { IsPlaceholder = true };
        return placeholder;
    }

    private static long s_placeholders;

    private static string StripArity(string name)
    {
        var tick = name.LastIndexOf('`');
        return tick < 0 ? name : name[..tick];
    }
}
