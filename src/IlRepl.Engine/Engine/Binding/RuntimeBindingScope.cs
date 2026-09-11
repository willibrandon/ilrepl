using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Binds accepted input using the live session and records exact runtime objects for emission.
/// </summary>
/// <remarks>
/// The scope an actual line binds in: the session's type table, its resolver, its generic
/// context, and reflection over what is loaded. This is the one scope that may load an
/// assembly, resolve a name through the runtime, or declare a member ahead of its line, because
/// the line it serves is being accepted. It remembers the runtime object behind every symbol it
/// hands out, so <see cref="RuntimeBindingAdapter"/> can give a bound result back to the emitter
/// as the object the resolver would have produced.
/// </remarks>
public sealed class RuntimeBindingScope : IBindingScope
{
    private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
        | BindingFlags.FlattenHierarchy;

    private readonly RuntimeBindingRegistry _registry;
    private readonly SymbolGenericContext _generics;

    /// <summary>
    /// Initializes a scope over a parse context.
    /// </summary>
    /// <param name="context">The parse context.</param>
    public RuntimeBindingScope(ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        _registry = new RuntimeBindingRegistry();
        _generics = new SymbolGenericContext([.. context.Generics.TypeArguments.Select(ImportType)],
            [.. context.Generics.MethodArguments.Select(ImportType)]);
    }

    private RuntimeBindingScope(ParseContext context, RuntimeBindingRegistry registry, SymbolGenericContext generics)
    {
        Context = context;
        _registry = registry;
        _generics = generics;
    }

    /// <summary>
    /// The parse context the scope answers from.
    /// </summary>
    public ParseContext Context { get; }

    /// <inheritdoc/>
    public IReadOnlyList<PropertySymbol> Properties(TypeSymbol declaring)
    {
        var type = TypeOf(declaring);
        return [.. type.GetProperties(AllMembers).Select(property => new PropertySymbol(
            DefinitionId.Loaded(RuntimeDefinitions.AssemblyInstance(property.Module.Assembly),
                property.Module.ModuleVersionId, property.MetadataToken),
            ImportType(property.DeclaringType!), property.Name, ImportType(property.PropertyType),
            property.GetIndexParameters().Select(parameter => ImportType(parameter.ParameterType)).ToArray(),
            property.GetAccessors(true).Any(accessor => accessor.IsPublic),
            property.GetAccessors(true).Any(accessor => accessor.IsStatic)))];
    }

    /// <inheritdoc/>
    public bool Inspecting => Context.Inspecting;

    /// <inheritdoc/>
    public SymbolGenericContext Generics => _generics;

    /// <inheritdoc/>
    public IBindingScope WithGenerics(SymbolGenericContext generics)
    {
        ArgumentNullException.ThrowIfNull(generics);
        return new RuntimeBindingScope(Context, _registry, generics);
    }

    /// <summary>
    /// Describes a runtime type as a symbol and remembers the type behind it.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The symbol.</returns>
    public TypeSymbol ImportType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var symbol = RuntimeSymbolImporter.Import(type);
        _registry.Types.TryAdd(symbol, type);
        return symbol;
    }

    /// <summary>
    /// Returns the imported runtime type or reconstructs it from the symbol's components.
    /// </summary>
    /// <remarks>
    /// The runtime type a symbol stands for: the type it was imported from when it was, otherwise
    /// the type built from its parts the way the type parser built it.
    /// </remarks>
    /// <param name="symbol">The symbol.</param>
    /// <returns>The type.</returns>
    /// <exception cref="InvalidOperationException">The symbol names a definition no runtime object stands for.</exception>
    public Type TypeOf(TypeSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (_registry.Types.TryGetValue(symbol, out var known))
        {
            return known;
        }

        switch (symbol.Kind)
        {
            case TypeSymbolKind.Primitive:
                return CilPrimitives.TypeOf(symbol.Keyword!);
            case TypeSymbolKind.Named:
                return RuntimeDefinitions.TypeOf(symbol.Definition) ?? throw new InvalidOperationException(
                    $"no runtime type stands for {SymbolRenderer.IlPath(symbol)}");
            case TypeSymbolKind.Constructed:
                return TypeOf(symbol.Element!).MakeGenericType([.. symbol.Arguments.Select(TypeOf)]);
            case TypeSymbolKind.TypeParameter:
            case TypeSymbolKind.MethodParameter:
                return ParameterTypeOf(symbol);
            case TypeSymbolKind.SzArray:
                return TypeOf(symbol.Element!).MakeArrayType();
            case TypeSymbolKind.Array:
                return TypeOf(symbol.Element!).MakeArrayType(symbol.Rank);
            case TypeSymbolKind.ByRef:
                return TypeOf(symbol.Element!).MakeByRefType();
            case TypeSymbolKind.Pointer:
                return TypeOf(symbol.Element!).MakePointerType();
            case TypeSymbolKind.FunctionPointer:
                // Reflection has no public API to construct a function pointer type, so the
                // signature is validated and the type is native int, which is how the evaluation
                // stack treats a method pointer anyway.
                return typeof(nint);
            case TypeSymbolKind.Modified:
            case TypeSymbolKind.Pinned:
                return TypeOf(symbol.Element!);
            default:
                throw new InvalidOperationException($"no runtime type for a {symbol.Kind} symbol");
        }
    }

    private static Type ParameterTypeOf(TypeSymbol symbol)
    {
        var isMethod = symbol.Kind == TypeSymbolKind.MethodParameter;
        if (RuntimeDefinitions.ParameterOf(symbol.Owner, isMethod, symbol.Position) is { } remembered)
        {
            return remembered;
        }

        if (isMethod)
        {
            var method = RuntimeDefinitions.MethodOf(symbol.Owner) as MethodInfo;
            if (method is { IsGenericMethodDefinition: true } && method.GetGenericArguments() is { } parameters
                && symbol.Position < parameters.Length)
            {
                return parameters[symbol.Position];
            }
        }
        else if (RuntimeDefinitions.TypeOf(symbol.Owner) is { IsGenericTypeDefinition: true } owner && owner.GetGenericArguments(
            ) is { } parameters && symbol.Position < parameters.Length)
        {
            return parameters[symbol.Position];
        }

        throw new InvalidOperationException($"no runtime type stands for the generic parameter {SymbolRenderer.Pretty(symbol)}");
    }

    /// <inheritdoc/>
    public TypeLookupResult LookupType(string name, string? assemblyHint, int writtenArity, bool valueTypeKeyword)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (assemblyHint is null or "ilrepl" && Context.Types.TryResolve(name, writtenArity > 0, valueTypeKeyword, out var sessionType))
        {
            return new TypeLookupResult(ImportType(sessionType), true);
        }

        if (assemblyHint == "ilrepl")
        {
            throw new ReplException($"no type '{name}' in the session (define one with .class)");
        }

        var lookupName = writtenArity > 0 && !name.Contains('`') ? name + "`" + SymbolRenderer.Number(writtenArity) : name;
        return new TypeLookupResult(ImportType(Context.Resolver.Resolve(lookupName, assemblyHint, Context)), false);
    }

    /// <inheritdoc/>
    public TypeSymbol LookupDecimal() => ImportType(typeof(decimal));

    /// <inheritdoc/>
    public IReadOnlyList<TypeSymbol> GenericArgumentsOf(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Kind == TypeSymbolKind.Constructed)
        {
            return type.Arguments;
        }

        var runtime = TypeOf(type);
        if (!runtime.IsGenericType)
        {
            return [];
        }

        var arguments = runtime.GetGenericArguments();
        return arguments is null ? [] : [.. arguments.Select(ImportType)];
    }

    /// <inheritdoc/>
    public bool TryGetDeclaration(TypeSymbol declaring, [System.Diagnostics.CodeAnalysis.NotNullWhen(
        true)] out IDeclarationMembers? members)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        if (Context.Types.TryGetMembers(TypeOf(declaring), out var own))
        {
            members = new RuntimeDeclarationMembers(this, own, declaring.DefinitionOrSelf);
            return true;
        }

        members = null;
        return false;
    }

    /// <inheritdoc/>
    public bool IsSessionType(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return TypeRelations.IsSessionType(TypeOf(type));
    }

    /// <inheritdoc/>
    public bool RequiresDefinitionLookup(TypeSymbol declaring)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        var type = TypeOf(declaring);
        return type.IsGenericType && type.GetGenericArguments().Any(ContainsBuilder);
    }

    private static bool ContainsBuilder(Type type) => type is GenericTypeParameterBuilder or TypeBuilder
        || type.HasElementType && ContainsBuilder(type.GetElementType()!)
        || type.IsConstructedGenericType && type.GetGenericArguments().Any(ContainsBuilder);

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> Methods(TypeSymbol declaring, string name)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        ArgumentNullException.ThrowIfNull(name);
        var type = TypeOf(declaring);
        if (RequiresDefinitionLookup(declaring))
        {
            // Types instantiated over a cell's generic parameters cannot be reflected over directly;
            // the definition answers, with the instantiation's arguments put in for its parameters.
            var definition = type.GetGenericTypeDefinition();
            return [.. definition.GetMethods(AllMembers)
                .Where(m => m.Name == name)
                .Select(m => Register(SymbolRelations.Instantiate(RuntimeSymbolImporter.Import(m), declaring, []),
                    new RuntimeDefinitionMember(m)))];
        }

        return [.. type.GetMethods(AllMembers).Where(m => m.Name == name).Select(m => Register(RuntimeSymbolImporter.Import(m), m))];
    }

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> AllMethods(TypeSymbol declaring)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        var type = TypeOf(declaring);
        if (RequiresDefinitionLookup(declaring))
        {
            var definition = type.GetGenericTypeDefinition();
            return [.. definition.GetMethods(AllMembers)
                .Select(m => Register(SymbolRelations.Instantiate(RuntimeSymbolImporter.Import(m), declaring, []),
                    new RuntimeDefinitionMember(m)))];
        }

        return [.. type.GetMethods(AllMembers).Select(m => Register(RuntimeSymbolImporter.Import(m), m))];
    }

    /// <inheritdoc/>
    public AccessContext Access
    {
        get
        {
            var scope = Context.Scope ?? AccessScope.Cell;
            return new AccessContext(scope.Type is null ? null : ImportType(scope.Type), scope.Description);
        }
    }

    /// <inheritdoc/>
    public IBindingScope ForSuggestions(out IDisposable? lease)
    {
        var snapshot = BindingSnapshot.Capture(Context);
        lease = snapshot;
        return new SnapshotBindingScope(snapshot);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> Constructors(TypeSymbol declaring, bool isStatic)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var type = TypeOf(declaring);
        if (RequiresDefinitionLookup(declaring))
        {
            var definition = type.GetGenericTypeDefinition();
            return [.. definition.GetConstructors(flags)
                .Select(c => Register(SymbolRelations.Instantiate(RuntimeSymbolImporter.Import(c), declaring, []),
                    new RuntimeDefinitionMember(c)))];
        }

        return [.. type.GetConstructors(flags).Select(c => Register(RuntimeSymbolImporter.Import(c), c))];
    }

    /// <inheritdoc/>
    public FieldSymbol? Field(TypeSymbol declaring, string name)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        ArgumentNullException.ThrowIfNull(name);
        var type = TypeOf(declaring);
        if (RequiresDefinitionLookup(declaring))
        {
            var definitionField = type.GetGenericTypeDefinition().GetField(name, AllMembers);
            return definitionField is null ? null : Register(
                SymbolRelations.Instantiate(RuntimeSymbolImporter.Import(definitionField), declaring),
                new RuntimeDefinitionField(definitionField));
        }

        var field = type.GetField(name, AllMembers);
        return field is null ? null : Register(RuntimeSymbolImporter.Import(field), field);
    }

    /// <inheritdoc/>
    public IReadOnlyList<FieldSymbol> Fields(TypeSymbol declaring)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        var type = TypeOf(declaring);
        if (RequiresDefinitionLookup(declaring))
        {
            type = type.GetGenericTypeDefinition();
        }

        return [.. type.GetFields(AllMembers).Select(f => Register(RuntimeSymbolImporter.Import(f), f))];
    }

    /// <inheritdoc/>
    public MethodSymbol? Instantiate(MethodSymbol definition, IReadOnlyList<TypeSymbol> arguments)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(arguments);
        if (PayloadOf(definition) is RuntimeDefinitionMember { Method: MethodInfo { IsGenericMethodDefinition: true } } member)
        {
            return GenericConstraints.SatisfiesMethod(definition, definition.DeclaringType, arguments, this)
                ? Register(SymbolRelations.Instantiate(definition, definition.DeclaringType, arguments), member) : null;
        }

        if (PayloadOf(definition) is not MethodInfo { IsGenericMethodDefinition: true } method)
        {
            return null;
        }

        MethodInfo closed;
        try
        {
            closed = method.MakeGenericMethod([.. arguments.Select(TypeOf)]);
        }
        catch (ArgumentException)
        {
            // Constraint violation; not a candidate.
            return null;
        }

        return Register(RuntimeSymbolImporter.Import(closed, definition.DeclaringType), closed);
    }

    /// <inheritdoc/>
    public TypeSymbol? BaseOf(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var runtime = TypeOf(type);
        var baseType = TypeRelations.BaseTypeOf(runtime, Context.Types);
        if (baseType is null)
        {
            return null;
        }

        var symbol = ImportType(baseType);
        RuntimeBindingObservations.RecordBase(runtime, symbol);
        return symbol;
    }

    /// <inheritdoc/>
    public IReadOnlyList<TypeSymbol> DeclaredInterfacesOf(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var runtime = TypeOf(type);
        var interfaces = TypeRelations.DeclaredInterfacesOf(runtime, Context.Types).Select(ImportType).ToArray();
        RuntimeBindingObservations.RecordInterfaces(runtime, interfaces);
        return interfaces;
    }

    /// <inheritdoc/>
    public IReadOnlyList<GenericParameterSymbol> GenericParameterDeclarations(TypeSymbol definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var runtime = TypeOf(definition.DefinitionOrSelf);
        if (!runtime.IsGenericTypeDefinition)
        {
            return [];
        }

        var parameters = runtime.GetGenericArguments();
        return parameters is null ? [] : [.. parameters.Select(RuntimeSymbolImporter.ImportParameter)];
    }

    /// <inheritdoc/>
    public GenericParameterSymbol? ParameterDeclaration(TypeSymbol parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!parameter.IsGenericParameter)
        {
            return null;
        }

        try
        {
            return RuntimeSymbolImporter.ImportParameter(TypeOf(parameter));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public TypeSymbol? EnumUnderlyingType(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var runtime = TypeOf(type);
        if (runtime.IsGenericParameter || runtime is System.Reflection.Emit.TypeBuilder || !runtime.IsEnum)
        {
            return null;
        }

        return ImportType(Enum.GetUnderlyingType(runtime));
    }

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> SessionMethods
    {
        get
        {
            _registry.SessionMethods ??= [.. Context.Methods.Select(signature => Register(RuntimeSymbolImporter.Import(signature, null,
                RuntimeDefinitions.OfDeclaration(signature, 0), MethodSymbolSource.Session, true), signature))];
            return _registry.SessionMethods;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<VariableSymbol> Locals
    {
        get
        {
            _registry.Locals ??= [.. Context.Locals.Select(l => new VariableSymbol(l.ExactType ?? ImportType(l.Type), l.Name,
                l.IsPinned))];
            return _registry.Locals;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<VariableSymbol> Arguments
    {
        get
        {
            _registry.Arguments ??= [.. Context.Arguments.Select(a => new VariableSymbol(a.ExactType ?? ImportType(a.Type), a.Name,
                false))];
            return _registry.Arguments;
        }
    }

    /// <inheritdoc/>
    public int ThisIndex => Context.ThisIndex;

    /// <inheritdoc/>
    public string Pretty(TypeSymbol? type)
    {
        if (type is null)
        {
            return "?";
        }

        try
        {
            return TypeNameFormatter.Pretty(TypeOf(type));
        }
        catch (InvalidOperationException)
        {
            return SymbolRenderer.Pretty(type);
        }
    }

    /// <inheritdoc/>
    public string Describe(MethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return PayloadOf(method) switch
        {
            MethodBase runtime => MemberResolver.Describe(runtime),
            RuntimeDefinitionMember definition => MemberResolver.Describe(definition.Method),
            _ => SymbolRenderer.Describe(method, Pretty),
        };
    }

    /// <summary>
    /// The runtime object behind a member symbol this scope handed out, or null.
    /// </summary>
    /// <param name="method">The member.</param>
    /// <returns>The <see cref="MethodBase"/>, <see cref="MethodSignature"/>, or payload record.</returns>
    internal object? PayloadOf(MethodSymbol method) =>
        _registry.Methods.TryGetValue(method, out var payload) ? payload
        : _registry.Declarations.TryGetValue(method.Definition, out var declared) ? declared
        : null;

    /// <summary>
    /// The runtime object behind a field symbol this scope handed out, or null.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>The <see cref="FieldInfo"/> or payload record.</returns>
    internal object? PayloadOf(FieldSymbol field) =>
        _registry.Fields.TryGetValue(field, out var payload) ? payload
        : _registry.Declarations.TryGetValue(field.Definition, out var declared) ? declared
        : null;

    /// <summary>
    /// Records the runtime object represented by a declared or loaded member symbol.
    /// </summary>
    /// <remarks>
    /// Remembers the runtime object behind a member symbol. A declared member is keyed by its
    /// definition, because a reference sees it through whatever construction and instantiation
    /// it names; a loaded member is keyed by the whole symbol, because reflection hands out a
    /// different object for each construction.
    /// </remarks>
    internal MethodSymbol Register(MethodSymbol symbol, object payload)
    {
        if (payload is MethodBase method)
        {
            RuntimeBindingObservations.Record(method, symbol);
        }
        else if (payload is RuntimeDefinitionMember definition)
        {
            RuntimeBindingObservations.Record(definition.Method, symbol);
        }

        if (payload is RuntimeDeclaredMember || symbol.Source == MethodSymbolSource.Session)
        {
            _registry.Declarations[symbol.Definition] = payload;
        }
        else
        {
            _registry.Methods[symbol] = payload;
        }

        return symbol;
    }

    internal FieldSymbol Register(FieldSymbol symbol, object payload)
    {
        if (payload is FieldInfo field)
        {
            RuntimeBindingObservations.Record(field, symbol);
        }
        else if (payload is RuntimeDefinitionField definition)
        {
            RuntimeBindingObservations.Record(definition.Field, symbol);
        }

        if (payload is RuntimeDeclaredField)
        {
            _registry.Declarations[symbol.Definition] = payload;
        }
        else
        {
            _registry.Fields[symbol] = payload;
        }

        return symbol;
    }

}
