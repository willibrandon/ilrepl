using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Everything a preview binds against, captured from the session at one moment and never
/// touched by the runtime again: the loaded assemblies as metadata under lease, the session's
/// type table and declarations as symbols, its methods, and the generic parameters, locals, and
/// arguments of the body being written. Binding against a snapshot loads nothing, resolves no
/// name through the runtime, and declares nothing on a real block.
/// </summary>
public sealed class BindingSnapshot : IDisposable
{
    private readonly List<MetadataLease> _leases = [];

    private BindingSnapshot(LoadedBindingCatalog catalog, IReadOnlyList<AssemblySymbolSource> searchOrder, AssemblySymbolSource? engine, AssemblySymbolSource? coreLib, HashSet<long> sessionAssemblies, SnapshotTypeTable types)
    {
        Catalog = catalog;
        SearchOrder = searchOrder;
        Engine = engine;
        CoreLib = coreLib;
        SessionAssemblies = sessionAssemblies;
        Types = types;
    }

    /// <summary>
    /// The assemblies references resolve through.
    /// </summary>
    public LoadedBindingCatalog Catalog { get; }

    /// <summary>
    /// The assemblies a name search visits, in the resolver's order.
    /// </summary>
    public IReadOnlyList<AssemblySymbolSource> SearchOrder { get; }

    /// <summary>
    /// The engine's own assembly, which a plain name lookup consults first, as <see cref="Type.GetType(string)"/> does.
    /// </summary>
    public AssemblySymbolSource? Engine { get; }

    /// <summary>
    /// The core library.
    /// </summary>
    public AssemblySymbolSource? CoreLib { get; }

    /// <summary>
    /// The instance numbers of the session's own loaded assemblies.
    /// </summary>
    public IReadOnlySet<long> SessionAssemblies { get; }

    /// <summary>
    /// The session's types by path, and the declarations of the ones being written.
    /// </summary>
    public SnapshotTypeTable Types { get; }

    /// <summary>
    /// The methods defined with <c>.method</c>.
    /// </summary>
    public IReadOnlyList<MethodSymbol> SessionMethods { get; private init; } = [];

    /// <summary>
    /// The generic parameters in scope for the body.
    /// </summary>
    public SymbolGenericContext Generics { get; private init; } = SymbolGenericContext.Empty;

    /// <summary>
    /// The locals of the body.
    /// </summary>
    public IReadOnlyList<VariableSymbol> Locals { get; private init; } = [];

    /// <summary>
    /// The arguments of the body.
    /// </summary>
    public IReadOnlyList<VariableSymbol> Arguments { get; private init; } = [];

    /// <summary>
    /// The argument index holding <c>this</c>, or -1.
    /// </summary>
    public int ThisIndex { get; private init; } = -1;

    /// <summary>
    /// True when the context only inspects.
    /// </summary>
    public bool Inspecting { get; private init; }

    /// <summary>
    /// Where accesses in the body are judged from.
    /// </summary>
    public AccessContext Access { get; private init; } = AccessContext.Cell;

    /// <summary>
    /// Captures the context of a session's current body.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>The snapshot.</returns>
    public static BindingSnapshot Capture(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var own = new List<Assembly>();
        foreach (var type in session.Types)
        {
            if (type.Definition is { } definition)
            {
                own.Add(definition.Assembly);
            }
        }

        foreach (var method in session.Methods)
        {
            own.Add(method.Trampoline.Definition.Assembly);
            own.Add(method.Version.Definition.Assembly);
        }

        return Capture(session.State.Context, own);
    }

    /// <summary>
    /// Captures a parse context: the assemblies its resolver searches, its type table, its
    /// methods, and its generic, local, and argument scope.
    /// </summary>
    /// <param name="context">The parse context.</param>
    /// <param name="sessionAssemblies">The session's own loaded assemblies, whose types the table names.</param>
    /// <returns>The snapshot.</returns>
    public static BindingSnapshot Capture(ParseContext context, IEnumerable<Assembly>? sessionAssemblies = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var leases = new List<MetadataLease>();
        var ordered = new List<(Assembly Assembly, AssemblySymbolSource Source)>();
        var searchOrder = new List<AssemblySymbolSource>();
        var sessionInstances = new HashSet<long>();

        void Take(Assembly assembly, bool searched)
        {
            var source = AssemblySymbolSource.For(assembly, context.Resolver.TryGetImage(assembly, out var image) ? image : null);
            if (source is null || ordered.Any(o => ReferenceEquals(o.Source, source)))
            {
                return;
            }

            leases.Add(source.Lease());
            ordered.Add((assembly, source));
            if (searched)
            {
                searchOrder.Add(source);
            }
        }

        // Reading the first assembly's metadata can itself bring the reader's assemblies into the
        // process, after the list was enumerated; a second pass takes what the first one loaded, in
        // the order the resolver would search them.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var assembly in context.Resolver.Assemblies.ToList())
            {
                Take(assembly, searched: true);
            }
        }

        foreach (var assembly in sessionAssemblies ?? [])
        {
            Take(assembly, searched: false);
            var source = AssemblySymbolSource.For(assembly);
            if (source is not null)
            {
                sessionInstances.Add(source.Instance);
            }
        }

        foreach (var (fullName, type) in context.Types.Entries)
        {
            if (!RuntimeDefinitions.IsDynamic(type))
            {
                Take(type.Assembly, searched: false);
                sessionInstances.Add(RuntimeDefinitions.AssemblyInstance(type.Assembly));
            }
        }

        var engineAssembly = typeof(TypeResolver).Assembly;
        var coreLibAssembly = typeof(object).Assembly;
        Take(engineAssembly, searched: false);
        Take(coreLibAssembly, searched: false);
        var catalog = new LoadedBindingCatalog(ordered);

        var openPaths = new List<string>();
        var dynamicEntries = context.Types.Entries.Where(e => RuntimeDefinitions.IsDynamic(e.Type) && context.Types.TryGetMembers(e.Type, out _)).ToList();
        foreach (var (fullName, _) in dynamicEntries)
        {
            openPaths.Add(fullName);
        }

        var sessionAssembly = dynamicEntries.Count > 0 ? RuntimeDefinitions.AssemblyInstance(dynamicEntries[0].Type.Assembly) : 0;
        var table = new SnapshotTypeTable(sessionAssembly, openPaths);
        foreach (var (fullName, type) in context.Types.Entries)
        {
            var symbol = RuntimeSymbolImporter.Import(type);
            DeclarationSymbol? declaration = null;
            if (RuntimeDefinitions.IsDynamic(type))
            {
                declaration = context.Types.TryGetMembers(type, out var members)
                    ? CopyDeclaration(symbol, type, members)
                    : new DeclarationSymbol(symbol, symbol.IsValueType ? RuntimeSymbolImporter.Import(typeof(ValueType)) : TypeSymbol.Object, [], [], [], [], false) { IsPlaceholder = true };
            }

            table.Add(fullName, symbol, declaration);
        }

        var snapshot = new BindingSnapshot(catalog, searchOrder, AssemblySymbolSource.For(engineAssembly), AssemblySymbolSource.For(coreLibAssembly), sessionInstances, table)
        {
            SessionMethods = [.. context.Methods.Select(signature => RuntimeSymbolImporter.Import(signature, null, RuntimeDefinitions.OfDeclaration(signature, 0), MethodSymbolSource.Session, true))],
            Generics = new SymbolGenericContext([.. context.Generics.TypeArguments.Select(RuntimeSymbolImporter.Import)], [.. context.Generics.MethodArguments.Select(RuntimeSymbolImporter.Import)]),
            Locals = [.. context.Locals.Select(l => new VariableSymbol(RuntimeSymbolImporter.Import(l.Type), l.Name, l.IsPinned))],
            Arguments = [.. context.Arguments.Select(a => new VariableSymbol(RuntimeSymbolImporter.Import(a.Type), a.Name, false))],
            ThisIndex = context.ThisIndex,
            Inspecting = context.Inspecting,
            Access = context.Scope is { } scope ? new AccessContext(scope.Type is null ? null : RuntimeSymbolImporter.Import(scope.Type), scope.Description) : AccessContext.Cell,
        };
        snapshot._leases.AddRange(leases);
        return snapshot;
    }

    private static DeclarationSymbol CopyDeclaration(TypeSymbol symbol, Type prototype, OwnMembers members)
    {
        IReadOnlyList<GenericParameterSymbol> parameters = [];
        if (prototype.IsGenericTypeDefinition && prototype.GetGenericArguments() is { } builders)
        {
            parameters = [.. builders.Select(RuntimeSymbolImporter.ImportParameter)];
        }

        return new DeclarationSymbol(
            symbol,
            members.BaseType is null ? null : RuntimeSymbolImporter.Import(members.BaseType),
            [.. members.Interfaces.Select(RuntimeSymbolImporter.Import)],
            parameters,
            members.Fields.Select(f => RuntimeSymbolImporter.Import(f.Declaration, symbol, RuntimeDefinitions.Of(f.Builder))),
            members.Methods.Select(m => RuntimeSymbolImporter.Import(m.Signature, symbol, RuntimeDefinitions.Of(m.Builder), m.Declared ? MethodSymbolSource.Declared : MethodSymbolSource.Forward, m.Declared)),
            members.DefineForward is not null);
    }

    /// <summary>
    /// Releases the holds on the assemblies read.
    /// </summary>
    public void Dispose()
    {
        foreach (var lease in _leases)
        {
            lease.Dispose();
        }

        _leases.Clear();
    }
}
