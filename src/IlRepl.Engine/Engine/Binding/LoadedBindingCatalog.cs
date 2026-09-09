using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;

namespace IlRepl.Engine.Binding;

/// <summary>
/// The assemblies a snapshot can see and how a reference from one reaches a definition in
/// another: by the requesting assembly's own load context first, then the default context, never
/// by running a load or a resolution handler. A reference that no rule settles stays unresolved
/// until an actual operation establishes it. Forwarders are followed from the assembly that
/// exports the type to the one that defines it, with cycles cut.
/// </summary>
public sealed class LoadedBindingCatalog
{
    private readonly List<AssemblySymbolSource> _sources = [];
    private readonly Dictionary<long, AssemblySymbolSource> _byInstance = [];
    private readonly Dictionary<long, LoadedContextIdentity> _contexts = [];
    private readonly Dictionary<long, IReadOnlyDictionary<int, TypeSymbol>> _observedTypes = [];
    private readonly Dictionary<string, List<AssemblySymbolSource>> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a catalog over assemblies, in the order a name search visits them.
    /// </summary>
    /// <param name="assemblies">The assemblies with their sources.</param>
    public LoadedBindingCatalog(IEnumerable<(Assembly Assembly, AssemblySymbolSource Source)> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var (assembly, source) in assemblies)
        {
            if (_byInstance.ContainsKey(source.Instance))
            {
                continue;
            }

            _sources.Add(source);
            _byInstance[source.Instance] = source;
            _contexts[source.Instance] = LoadedContextIdentity.Of(AssemblyLoadContext.GetLoadContext(assembly));
            _observedTypes[source.Instance] = RuntimeBindingObservations.Capture(assembly);
            if (!_byName.TryGetValue(source.Name, out var same))
            {
                same = [];
                _byName[source.Name] = same;
            }

            same.Add(source);
        }
    }

    /// <summary>
    /// The sources, in search order.
    /// </summary>
    public IReadOnlyList<AssemblySymbolSource> Sources => _sources;

    /// <summary>
    /// The source of an assembly instance, or null when the catalog does not hold it.
    /// </summary>
    /// <param name="instance">The instance number.</param>
    /// <returns>The source, or null.</returns>
    public AssemblySymbolSource? Source(long instance) => _byInstance.TryGetValue(instance, out var source) ? source : null;

    /// <summary>
    /// The core library, when the catalog holds it.
    /// </summary>
    public AssemblySymbolSource? CoreLib => _sources.FirstOrDefault(s => s.IsCoreLib);

    /// <summary>
    /// The source of the assembly a definition belongs to; the core library for a primitive.
    /// </summary>
    /// <param name="type">A named definition, a primitive, or a construction.</param>
    /// <returns>The source, or null for a declaration or an assembly outside the catalog.</returns>
    public AssemblySymbolSource? SourceOf(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = type.DefinitionOrSelf;
        return definition.Kind switch
        {
            TypeSymbolKind.Primitive => CoreLib,
            TypeSymbolKind.Named when !definition.Definition.IsDeclaration => Source(definition.Definition.Assembly),
            _ => null,
        };
    }

    /// <summary>
    /// The definition handle of a named type or a primitive, with its source.
    /// </summary>
    /// <param name="type">A named definition, a primitive, or a construction.</param>
    /// <returns>The source and handle, or null.</returns>
    public (AssemblySymbolSource Source, TypeDefinitionHandle Handle)? Locate(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = type.DefinitionOrSelf;
        var source = SourceOf(type);
        if (source is null)
        {
            return null;
        }

        if (definition.Kind == TypeSymbolKind.Primitive)
        {
            var coreLibName = CilPrimitives.CoreLibNameOf(definition.Keyword!);
            return source.Index.TryGetDefinition("System", coreLibName["System.".Length..], out var primitive) ? (source, primitive) : null;
        }

        var handle = source.TypeHandleOf(definition.Definition);
        return handle is { } h ? (source, h) : null;
    }

    /// <summary>
    /// The first assembly with a simple name, case-insensitively, in search order.
    /// </summary>
    /// <param name="simpleName">The name.</param>
    /// <returns>The source, or null.</returns>
    public AssemblySymbolSource? FindAssembly(string simpleName)
    {
        ArgumentNullException.ThrowIfNull(simpleName);
        foreach (var source in _sources)
        {
            if (string.Equals(source.Name, simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>
    /// The assembly a reference from another assembly binds to: one with that name in the
    /// requester's own load context, else one in the default context. Two candidates in the same
    /// context, or none, leave the reference unresolved.
    /// </summary>
    /// <param name="requester">The assembly making the reference.</param>
    /// <param name="reference">The reference.</param>
    /// <returns>The source, or null.</returns>
    public AssemblySymbolSource? ResolveReference(AssemblySymbolSource requester, AssemblyReferenceHandle reference)
    {
        ArgumentNullException.ThrowIfNull(requester);
        var requested = LoadedAssemblyIdentity.Of(requester.Reader.GetAssemblyReference(reference).GetAssemblyName());
        var name = requested.Name;
        if (!_byName.TryGetValue(name, out var candidates))
        {
            return null;
        }

        if (!_contexts.TryGetValue(requester.Instance, out var requesterContext))
        {
            return null;
        }

        var compatible = candidates.Where(candidate => candidate.Identity.Satisfies(requested)).ToArray();
        if (requesterContext.IsSession && SessionAssemblies.IsSessionName(name))
        {
            // A session assembly's context resolves session names through the session registry,
            // whatever context the definition landed in; the names are unique, so one candidate is the one.
            var session = compatible.Where(candidate => _contexts[candidate.Instance].IsSession).ToList();
            return session.Count == 1 ? session[0] : null;
        }

        var own = compatible.Where(candidate => _contexts[candidate.Instance].Id == requesterContext.Id).ToList();
        if (own.Count == 1)
        {
            return own[0];
        }

        if (own.Count > 1)
        {
            return null;
        }

        if (!requesterContext.UsesDefaultFallback)
        {
            return name == "System.Private.CoreLib" ? compatible.SingleOrDefault(candidate => candidate.IsCoreLib) : null;
        }

        var fallback = compatible.Where(candidate => _contexts[candidate.Instance].IsDefault).ToList();
        return fallback.Count == 1 ? fallback[0] : null;
    }

    /// <summary>
    /// The definition a type reference names, through assembly references and forwarders.
    /// </summary>
    /// <param name="requester">The assembly making the reference.</param>
    /// <param name="handle">The reference.</param>
    /// <returns>The definition, or null when it cannot be settled.</returns>
    public TypeSymbol? ResolveTypeReference(AssemblySymbolSource requester, TypeReferenceHandle handle)
    {
        ArgumentNullException.ThrowIfNull(requester);
        return ResolveTypeReference(requester, handle, []);
    }

    private TypeSymbol? ResolveTypeReference(
        AssemblySymbolSource requester, TypeReferenceHandle handle, HashSet<TypeReferenceHandle> parents)
    {
        if (!parents.Add(handle))
        {
            return null;
        }

        if (_observedTypes.TryGetValue(requester.Instance, out var observed)
            && observed.TryGetValue(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(handle), out var actual)
            && SourceOf(actual) is not null)
        {
            return actual;
        }

        var (ns, name, scope) = requester.ReferenceFacts(handle);
        switch (scope.Kind)
        {
            case HandleKind.TypeReference:
            {
                var outer = ResolveTypeReference(requester, (TypeReferenceHandle)scope, parents);
                return outer is null ? null : FindNestedType(outer, name);
            }

            case HandleKind.AssemblyReference:
            {
                var target = ResolveReference(requester, (AssemblyReferenceHandle)scope);
                return target is null ? null : FindType(target, ns, name);
            }

            case HandleKind.ModuleDefinition:
            case HandleKind.ModuleReference:
            default:
                return FindType(requester, ns, name);
        }
    }

    /// <summary>
    /// The top-level type an assembly defines or exports under a name, following forwarders to
    /// the defining assembly, as <see cref="Assembly.GetType(string)"/> would find it.
    /// </summary>
    /// <param name="source">The assembly asked.</param>
    /// <param name="ns">The namespace, or empty.</param>
    /// <param name="name">The metadata name, arity suffix included.</param>
    /// <returns>The definition, or null.</returns>
    public TypeSymbol? FindType(AssemblySymbolSource source, string ns, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(name);
        return FindType(source, ns, name, null);
    }

    private TypeSymbol? FindType(AssemblySymbolSource source, string ns, string name, HashSet<(long, string, string)>? visited)
    {
        if (source.Index.TryGetDefinition(ns, name, out var handle))
        {
            return source.Definition(handle);
        }

        if (!source.Index.TryGetForwarder(ns, name, out var forwarder))
        {
            return null;
        }

        visited ??= [];
        if (!visited.Add((source.Instance, ns, name)))
        {
            return null;
        }

        var target = ResolveReference(source, forwarder);
        return target is null ? null : FindType(target, ns, name, visited);
    }

    /// <summary>
    /// A type nested in a definition, by name.
    /// </summary>
    /// <param name="outer">The enclosing definition.</param>
    /// <param name="name">The nested type's metadata name.</param>
    /// <returns>The nested definition, or null.</returns>
    public TypeSymbol? FindNestedType(TypeSymbol outer, string name)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(name);
        var located = Locate(outer);
        if (located is not { } l)
        {
            return null;
        }

        return l.Source.Index.TryGetNested(l.Handle, name, out var nested) ? l.Source.Definition(nested) : null;
    }

    /// <summary>
    /// The nested definitions of a type, in declaration order.
    /// </summary>
    /// <param name="outer">The enclosing definition.</param>
    /// <returns>The nested definitions.</returns>
    public IReadOnlyList<TypeSymbol> NestedTypesOf(TypeSymbol outer)
    {
        ArgumentNullException.ThrowIfNull(outer);
        var located = Locate(outer);
        return located is { } l ? [.. l.Source.Index.NestedOf(l.Handle).Select(l.Source.Definition)] : [];
    }

    /// <summary>
    /// Resolves an IL type path against one assembly: the top-level type, then each nested segment.
    /// </summary>
    /// <param name="source">The assembly asked.</param>
    /// <param name="ns">The namespace of the top-level type, or empty.</param>
    /// <param name="name">The top-level type's metadata name.</param>
    /// <param name="nested">The nested segments, outermost first.</param>
    /// <returns>The definition, or null.</returns>
    public TypeSymbol? FindPath(AssemblySymbolSource source, string ns, string name, IReadOnlyList<string> nested)
    {
        ArgumentNullException.ThrowIfNull(nested);
        var current = FindType(source, ns, name);
        foreach (var segment in nested)
        {
            if (current is null)
            {
                return null;
            }

            current = FindNestedType(current, segment);
        }

        return current;
    }
}
