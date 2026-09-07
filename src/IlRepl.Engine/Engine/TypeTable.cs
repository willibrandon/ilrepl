namespace IlRepl.Engine;

/// <summary>
/// The types a session has defined with <c>.class</c>, and the ones being written, as a name
/// lookup the type parser consults before any assembly. Session types shadow framework short
/// names, as types in one ILAsm module shadow the ones it references.
/// </summary>
public sealed class TypeTable
{
    private readonly List<(string FullName, string ShortName, Type Type)> _entries = [];
    // Keyed by identity: a builder's Equals may ask for an underlying type it does not have yet.
    private readonly Dictionary<Type, OwnMembers> _members = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A table with no types.
    /// </summary>
    public static TypeTable Empty { get; } = new();

    /// <summary>
    /// The types in the table, in the order they were added.
    /// </summary>
    public IReadOnlyList<Type> Types => [.. _entries.Select(e => e.Type)];

    /// <summary>
    /// Adds a type under its ILAsm path.
    /// </summary>
    /// <param name="fullName">The path: <c>Geometry.Point</c>, <c>Outer/Inner</c>, <c>Box`1</c>.</param>
    /// <param name="type">The runtime type, or the prototype builder of a type still being written.</param>
    public void Add(string fullName, Type type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentNullException.ThrowIfNull(type);
        var slash = fullName.LastIndexOf('/');
        var dot = fullName.LastIndexOf('.');
        var shortName = fullName[(Math.Max(slash, dot) + 1)..];
        _entries.RemoveAll(e => e.FullName == fullName);
        _entries.Add((fullName, shortName, type));
    }

    /// <summary>
    /// Removes a type by its path, with the members registered for it.
    /// </summary>
    /// <param name="fullName">The path.</param>
    public void Remove(string fullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        foreach (var entry in _entries.Where(e => e.FullName == fullName).ToList())
        {
            _members.Remove(entry.Type);
        }

        _entries.RemoveAll(e => e.FullName == fullName);
    }

    /// <summary>
    /// A callback that resolves a name inside the family being written to a placeholder, for a
    /// nested type referenced before its declaration, or returns null.
    /// </summary>
    public Func<string, bool, Type?>? Forward { get; set; }

    /// <summary>
    /// Registers the members of a type being written, so references to them resolve through
    /// the declarations rather than the builders.
    /// </summary>
    /// <param name="prototype">The type's prototype builder.</param>
    /// <param name="members">Its members so far.</param>
    public void SetMembers(Type prototype, OwnMembers members)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        ArgumentNullException.ThrowIfNull(members);
        _members[prototype] = members;
    }

    /// <summary>
    /// Finds the members of a type being written.
    /// </summary>
    /// <param name="type">The type, its prototype, or an instantiation of it.</param>
    /// <param name="members">The members.</param>
    /// <returns>True when the type is being written.</returns>
    public bool TryGetMembers(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out OwnMembers? members)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;
        return _members.TryGetValue(definition, out members);
    }

    /// <summary>
    /// Copies the table.
    /// </summary>
    /// <returns>An independent copy.</returns>
    public TypeTable Clone()
    {
        var copy = new TypeTable { Forward = Forward };
        copy._entries.AddRange(_entries);
        foreach (var pair in _members)
        {
            copy._members[pair.Key] = pair.Value;
        }

        return copy;
    }

    /// <summary>
    /// Finds a type by the name written in IL: the exact path, or a short name when it is unique.
    /// The most recently added entry wins an exact match, so an open type shadows an accepted one.
    /// </summary>
    /// <param name="name">The name as written, with its arity suffix if any.</param>
    /// <param name="withArguments">True when type arguments follow, so a name without a suffix may match a generic type.</param>
    /// <param name="valueType">True when the reference was written with <c>valuetype</c>, which decides the kind of a placeholder.</param>
    /// <param name="type">The type found.</param>
    /// <returns>True when a session type matched.</returns>
    /// <exception cref="ReplException">A short name matched more than one type.</exception>
    public bool TryResolve(string name, bool withArguments, bool valueType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? type)
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
            type = name.Contains('/') ? Forward?.Invoke(name, valueType) : null;
            return type is not null;
        }

        var matches = _entries.Where(e => e.ShortName == name || (withArguments && StripArity(e.ShortName) == name)).Select(e => e.Type).Distinct().ToList();
        if (matches.Count == 1)
        {
            type = matches[0];
            return true;
        }

        if (matches.Count > 1)
        {
            throw new ReplException($"'{name}' is ambiguous: {string.Join(", ", _entries.Where(e => matches.Contains(e.Type)).Select(e => e.FullName))} (write the full name)");
        }

        return false;
    }

    private static string StripArity(string name)
    {
        var tick = name.LastIndexOf('`');
        return tick < 0 ? name : name[..tick];
    }
}
