using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Binds previews from captured metadata and declarations without runtime resolution or builders.
/// </summary>
/// <remarks>
/// The scope a preview binds in: a <see cref="BindingSnapshot"/> and nothing else. Names are found
/// in the same order the resolver searches, members are listed as reflection would list them, and
/// every answer comes from captured metadata and copied declarations. Nothing here loads,
/// resolves through the runtime, or touches a builder.
/// </remarks>
public sealed class SnapshotBindingScope : IBindingScope
{
    private readonly BindingSnapshot _snapshot;
    private readonly SnapshotBindingState _shared;
    private readonly SymbolGenericContext _generics;
    private readonly bool _confirming;

    /// <summary>
    /// Initializes a scope over a snapshot, with the snapshot's own generic context in scope.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    public SnapshotBindingScope(BindingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _shared = new SnapshotBindingState(snapshot.Types.Declarations.ToDictionary(p => p.Key, p => p.Value.Clone()));
        _generics = snapshot.Generics;
    }

    private SnapshotBindingScope(
        BindingSnapshot snapshot, SnapshotBindingState shared, SymbolGenericContext generics, bool confirming = false)
    {
        _snapshot = snapshot;
        _shared = shared;
        _generics = generics;
        _confirming = confirming;
    }

    /// <summary>
    /// The snapshot.
    /// </summary>
    public BindingSnapshot Snapshot => _snapshot;

    /// <summary>
    /// Retains symbolic forward references after an editing line has been accepted.
    /// </summary>
    internal void CommitDeclarations()
    {
        foreach (var declaration in _shared.Declarations.Values)
        {
            _snapshot.Types.Add(SymbolRenderer.IlPath(declaration.Type), declaration.Type, declaration.Clone());
        }
    }

    /// <inheritdoc/>
    public bool Inspecting => _snapshot.Inspecting;

    /// <inheritdoc/>
    public SymbolGenericContext Generics => _generics;

    /// <inheritdoc/>
    public IBindingScope WithGenerics(SymbolGenericContext generics)
    {
        ArgumentNullException.ThrowIfNull(generics);
        return new SnapshotBindingScope(_snapshot, _shared, generics, _confirming);
    }

    /// <summary>
    /// Creates a scope that confirms existing candidates without declaring placeholders or generating typo suggestions.
    /// </summary>
    /// <returns>The read-only confirmation scope.</returns>
    public SnapshotBindingScope ForConfirmation() => new(_snapshot, _shared, _generics, confirming: true);

    /// <inheritdoc/>
    public TypeLookupResult LookupType(string name, string? assemblyHint, int writtenArity, bool valueTypeKeyword)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (assemblyHint is null or "ilrepl"
            && _snapshot.Types.TryResolve(name, writtenArity > 0, valueTypeKeyword, out var sessionType, !_confirming))
        {
            if (sessionType.Definition.IsDeclaration && !_shared.Declarations.ContainsKey(sessionType.Definition)
                && _snapshot.Types.DeclarationOf(sessionType) is { } placeholder)
            {
                _shared.Declarations[sessionType.Definition] = placeholder.Clone();
            }

            return new TypeLookupResult(sessionType, true);
        }

        if (assemblyHint == "ilrepl")
        {
            throw new ReplException($"no type '{name}' in the session (define one with .class)");
        }

        var lookupName = writtenArity > 0 && !name.Contains('`') ? name + "`" + SymbolRenderer.Number(writtenArity) : name;
        return new TypeLookupResult(Resolve(lookupName, assemblyHint), false);
    }

    /// <summary>
    /// Finds a type in the snapshot using the runtime resolver's assembly and namespace search order.
    /// </summary>
    private TypeSymbol Resolve(string ilName, string? assemblyHint)
    {
        var segments = ilName.Split('/');
        var top = segments[0];
        var dot = top.LastIndexOf('.');
        var ns = dot < 0 ? "" : top[..dot];
        var typeName = dot < 0 ? top : top[(dot + 1)..];
        var nested = segments.Length > 1 ? segments[1..] : [];

        if (assemblyHint is not null && _snapshot.Catalog.FindAssembly(assemblyHint) is { } hinted)
        {
            if (_snapshot.Catalog.FindPath(hinted, ns, typeName, nested) is { } fromHint)
            {
                return fromHint;
            }

            // Reference facades forward most types elsewhere; fall through to a global search.
        }

        if (FindEverywhere(ns, typeName, nested) is { } found)
        {
            return found;
        }

        if (!ilName.Contains('.'))
        {
            foreach (var common in TypeResolver.CommonNamespaces)
            {
                if (FindEverywhere(common, typeName, nested) is { } inCommon)
                {
                    return inCommon;
                }
            }

            if (nested.Length == 0)
            {
                var matches = new List<TypeSymbol>();
                foreach (var source in _snapshot.SearchOrder)
                {
                    foreach (var handle in source.Index.VisibleNamed(typeName))
                    {
                        var candidate = source.Definition(handle);
                        if (!matches.Contains(candidate))
                        {
                            matches.Add(candidate);
                        }
                    }
                }

                if (matches.Count == 1)
                {
                    return matches[0];
                }

                if (matches.Count > 1)
                {
                    throw new ReplException(
                        $"'{ilName}' is ambiguous: {string.Join(", ", matches.Select(SymbolRenderer.ReflectionFullName).Take(6))}");
                }
            }
        }

        var hint = assemblyHint is null ? "" : $" in [{assemblyHint}]";
        var suggestion = _confirming ? null
            : NameSuggestions.NearestType(ilName, assemblyHint, _shared.Index ??= new TypeIndex(_snapshot), Access, this);
        var advice = suggestion is null ? " (load its assembly with .load)" : NameSuggestions.Parenthetical(suggestion.Spelling);
        throw new ReplException($"type '{ilName}' not found{hint}{advice}");
    }

    private TypeSymbol? FindEverywhere(string ns, string typeName, string[] nested)
    {
        // Type.GetType looks in the calling assembly and the core library before anything else.
        if (_snapshot.Engine is { } engine && _snapshot.Catalog.FindPath(engine, ns, typeName, nested) is { } inEngine)
        {
            return inEngine;
        }

        if (_snapshot.CoreLib is { } coreLib && _snapshot.Catalog.FindPath(coreLib, ns, typeName, nested) is { } inCoreLib)
        {
            return inCoreLib;
        }

        foreach (var source in _snapshot.SearchOrder)
        {
            if (_snapshot.Catalog.FindPath(source, ns, typeName, nested) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public TypeSymbol LookupDecimal() => CoreLibType("Decimal") ?? throw new ReplException(
        "type 'System.Decimal' not found (load its assembly with .load)");

    private TypeSymbol? CoreLibType(string name) => _snapshot.CoreLib is { } coreLib ? _snapshot.Catalog.FindType(coreLib, "System",
        name) : null;

    /// <inheritdoc/>
    public IReadOnlyList<TypeSymbol> GenericArgumentsOf(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Kind == TypeSymbolKind.Constructed ? type.Arguments : [.. GenericParameterDeclarations(type).Select(p => p.AsType)];
    }

    /// <inheritdoc/>
    public bool TryGetDeclaration(TypeSymbol declaring, [System.Diagnostics.CodeAnalysis.NotNullWhen(
        true)] out IDeclarationMembers? members)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        if (_shared.Declarations.TryGetValue(declaring.DefinitionOrSelf.Definition, out var declaration))
        {
            members = new SnapshotDeclarationMembers(declaration, !_confirming);
            return true;
        }

        members = null;
        return false;
    }

    /// <inheritdoc/>
    public bool IsSessionType(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = SymbolRelations.InnermostElement(type).DefinitionOrSelf;
        if (definition.Kind != TypeSymbolKind.Named)
        {
            return false;
        }

        return definition.Definition.IsDeclaration || _snapshot.SessionAssemblies.Contains(definition.Definition.Assembly);
    }

    /// <inheritdoc/>
    public bool RequiresDefinitionLookup(TypeSymbol declaring) => false;

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> Methods(TypeSymbol declaring, string name)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        ArgumentNullException.ThrowIfNull(name);
        return [.. AllMethodsOf(declaring).Where(m => m.Name == name)];
    }

    /// <summary>
    /// The methods offered by a type and its bases under reflection's visibility and override rules.
    /// </summary>
    private List<MethodSymbol> AllMethodsOf(TypeSymbol declaring)
    {
        var collected = new List<MethodSymbol>();
        var first = true;
        for (var current = declaring; current is not null; current = BaseOf(current))
        {
            foreach (var method in DeclaredMethods(current))
            {
                if (method.IsConstructor)
                {
                    continue;
                }

                if (!first)
                {
                    var access = method.Attributes & MethodAttributes.MemberAccessMask;
                    if (method.IsStatic ? access is not (
                        MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem)
                            : access is MethodAttributes.Private or MethodAttributes.PrivateScope)
                    {
                        continue;
                    }

                    if (method.IsVirtual && collected.Any(c => c.IsVirtual && c.Name == method.Name && SameShape(c, method)))
                    {
                        continue;
                    }
                }

                collected.Add(method);
            }

            if (current.IsInterface)
            {
                break;
            }

            first = false;
        }

        return collected;
    }

    private static bool SameShape(MethodSymbol a, MethodSymbol b) =>
        a.GenericParameters.Count == b.GenericParameters.Count && a.Parameters.Count == b.Parameters.Count
        && a.Parameters.Zip(b.Parameters).All(p => SymbolIdentity.Equal(p.First.Type, p.Second.Type));

    private IReadOnlyList<MethodSymbol> DeclaredMethods(TypeSymbol type)
    {
        var definition = type.DefinitionOrSelf;
        var declared = DefinitionMethods(definition);
        return type.Kind == TypeSymbolKind.Constructed ? [.. declared.Select(m => SymbolRelations.Instantiate(m, type, []))] : declared;
    }

    private IReadOnlyList<MethodSymbol> DefinitionMethods(TypeSymbol definition)
    {
        if (_shared.Declarations.TryGetValue(definition.Definition, out var declaration))
        {
            return declaration.Methods;
        }

        if (_shared.Methods.TryGetValue(definition, out var known))
        {
            return known;
        }

        var located = _snapshot.Catalog.Locate(definition);
        var methods = located is { } l ? l.Source.Methods(l.Handle, _snapshot.Catalog) : [];
        _shared.Methods[definition] = methods;
        return methods;
    }

    private IReadOnlyList<FieldSymbol> DefinitionFields(TypeSymbol definition)
    {
        if (_shared.Declarations.TryGetValue(definition.Definition, out var declaration))
        {
            return declaration.Fields;
        }

        if (_shared.Fields.TryGetValue(definition, out var known))
        {
            return known;
        }

        var located = _snapshot.Catalog.Locate(definition);
        var fields = located is { } l ? l.Source.Fields(l.Handle, _snapshot.Catalog) : [];
        _shared.Fields[definition] = fields;
        return fields;
    }

    private IEnumerable<FieldSymbol> AllFields(TypeSymbol declaring)
    {
        var first = true;
        for (var current = declaring; current is not null; current = BaseOf(current))
        {
            foreach (var field in DefinitionFields(current.DefinitionOrSelf))
            {
                var seen = current.Kind == TypeSymbolKind.Constructed ? SymbolRelations.Instantiate(field, current) : field;
                if (!first)
                {
                    var access = field.Attributes & FieldAttributes.FieldAccessMask;
                    if (field.IsStatic ? access is not (
                        FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem) : access is FieldAttributes.Private
                            or FieldAttributes.PrivateScope)
                    {
                        continue;
                    }
                }

                yield return seen;
            }

            if (current.IsInterface)
            {
                break;
            }

            first = false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> AllMethods(TypeSymbol declaring)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        return AllMethodsOf(declaring);
    }

    /// <inheritdoc/>
    public AccessContext Access => _snapshot.Access;

    /// <inheritdoc/>
    public IBindingScope ForSuggestions(out IDisposable? lease)
    {
        lease = null;
        return this;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> Constructors(TypeSymbol declaring, bool isStatic)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        var name = isStatic ? ".cctor" : ".ctor";
        return [.. DeclaredMethods(declaring).Where(m => m.Name == name)];
    }

    /// <inheritdoc/>
    public FieldSymbol? Field(TypeSymbol declaring, string name)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        ArgumentNullException.ThrowIfNull(name);
        return AllFields(declaring).FirstOrDefault(f => f.Name == name);
    }

    /// <inheritdoc/>
    public IReadOnlyList<FieldSymbol> Fields(TypeSymbol declaring)
    {
        ArgumentNullException.ThrowIfNull(declaring);
        return [.. AllFields(declaring)];
    }

    /// <inheritdoc/>
    public IReadOnlyList<PropertySymbol> Properties(TypeSymbol declaring)
    {
        var properties = new List<PropertySymbol>();
        var visited = new HashSet<TypeSymbol>();
        for (var type = declaring; type is not null && visited.Add(type); type = BaseOf(type))
        {
            IReadOnlyList<PropertySymbol> declared;
            if (_shared.Declarations.TryGetValue(type.DefinitionOrSelf.Definition, out var declaration))
            {
                declared = declaration.Properties;
            }
            else
            {
                var located = _snapshot.Catalog.Locate(type.DefinitionOrSelf);
                declared = located is { } metadata ? metadata.Source.Properties(metadata.Handle, _snapshot.Catalog) : [];
            }

            foreach (var property in declared)
            {
                properties.Add(property with
                {
                    DeclaringType = type,
                    Type = SymbolRelations.SubstituteFor(type, property.Type),
                    Parameters = [.. property.Parameters.Select(parameter => SymbolRelations.SubstituteFor(type, parameter))],
                });
            }
        }

        return properties;
    }

    /// <inheritdoc/>
    public MethodSymbol? Instantiate(MethodSymbol definition, IReadOnlyList<TypeSymbol> arguments)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!GenericConstraints.SatisfiesMethod(definition, definition.DeclaringType, arguments, this))
        {
            return null;
        }

        return SymbolRelations.Instantiate(definition, definition.DeclaringType, arguments);
    }

    /// <inheritdoc/>
    public TypeSymbol? BaseOf(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        switch (type.Kind)
        {
            case TypeSymbolKind.TypeParameter:
            case TypeSymbolKind.MethodParameter:
            {
                var declaration = ParameterDeclaration(type);
                return declaration?.Constraints.FirstOrDefault(c => !c.IsInterface && !c.IsGenericParameter) ?? TypeSymbol.Object;
            }

            case TypeSymbolKind.SzArray:
            case TypeSymbolKind.Array:
                return CoreLibType("Array");
            case TypeSymbolKind.ByRef:
            case TypeSymbolKind.Pointer:
            case TypeSymbolKind.FunctionPointer:
            case TypeSymbolKind.Unresolved:
                return null;
            case TypeSymbolKind.Modified:
            case TypeSymbolKind.Pinned:
                return BaseOf(type.Element!);
            default:
                break;
        }

        var definition = type.DefinitionOrSelf;
        TypeSymbol? baseType;
        if (_shared.Declarations.TryGetValue(definition.Definition, out var declared))
        {
            baseType = declared.BaseType;
        }
        else if (_snapshot.Catalog.Locate(definition) is { } located)
        {
            baseType = located.Source.BaseType(located.Handle, _snapshot.Catalog);
        }
        else
        {
            return null;
        }

        return baseType is null ? null : SymbolRelations.SubstituteFor(type, baseType);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TypeSymbol> DeclaredInterfacesOf(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsGenericParameter)
        {
            return ParameterDeclaration(type)?.Constraints.Where(c => c.IsInterface).ToList() ?? [];
        }

        if (type.Kind is not (TypeSymbolKind.Named or TypeSymbolKind.Constructed or TypeSymbolKind.Primitive))
        {
            return [];
        }

        var definition = type.DefinitionOrSelf;
        IReadOnlyList<TypeSymbol> declaredInterfaces;
        if (_shared.Declarations.TryGetValue(definition.Definition, out var declared))
        {
            declaredInterfaces = declared.Interfaces;
        }
        else if (_snapshot.Catalog.Locate(definition) is { } located)
        {
            declaredInterfaces = located.Source.Interfaces(located.Handle, _snapshot.Catalog);
        }
        else
        {
            return [];
        }

        return [.. declaredInterfaces.Select(i => SymbolRelations.SubstituteFor(type, i))];
    }

    /// <inheritdoc/>
    public IReadOnlyList<GenericParameterSymbol> GenericParameterDeclarations(TypeSymbol definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var target = definition.DefinitionOrSelf;
        if (target.Kind != TypeSymbolKind.Named || target.GenericParameterNames.Count == 0)
        {
            return [];
        }

        if (_shared.Declarations.TryGetValue(target.Definition, out var declared))
        {
            return declared.GenericParameters;
        }

        if (_shared.Parameters.TryGetValue(target.Definition, out var known))
        {
            return known;
        }

        var located = _snapshot.Catalog.Locate(target);
        var parameters = located is { } l ? l.Source.GenericParameters(l.Handle, _snapshot.Catalog) : [];
        _shared.Parameters[target.Definition] = parameters;
        return parameters;
    }

    /// <inheritdoc/>
    public GenericParameterSymbol? ParameterDeclaration(TypeSymbol parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!parameter.IsGenericParameter)
        {
            return null;
        }

        if (parameter.Kind == TypeSymbolKind.TypeParameter)
        {
            if (_shared.Declarations.TryGetValue(parameter.Owner, out var declared))
            {
                return parameter.Position < declared.GenericParameters.Count ? declared.GenericParameters[parameter.Position] : null;
            }

            var source = _snapshot.Catalog.Source(parameter.Owner.Assembly);
            var handle = source?.TypeHandleOf(parameter.Owner);
            if (source is not null && handle is { } h)
            {
                var parameters = source.GenericParameters(h, _snapshot.Catalog);
                return parameter.Position < parameters.Count ? parameters[parameter.Position] : null;
            }

            return null;
        }

        foreach (var declaration in _shared.Declarations.Values)
        {
            foreach (var method in declaration.Methods)
            {
                if (method.Definition == parameter.Owner)
                {
                    return parameter.Position < method.GenericParameters.Count ? method.GenericParameters[parameter.Position] : null;
                }
            }
        }

        var methodSource = _snapshot.Catalog.Source(parameter.Owner.Assembly);
        if (methodSource is null || parameter.Owner.IsDeclaration)
        {
            return null;
        }

        var methodHandle = MetadataTokens.EntityHandle(parameter.Owner.Token);
        if (methodHandle.Kind != HandleKind.MethodDefinition)
        {
            return null;
        }

        var declaring = methodSource.Definition(methodSource.Reader.GetMethodDefinition((
            MethodDefinitionHandle)methodHandle).GetDeclaringType());
        var owner = DefinitionMethods(declaring).FirstOrDefault(m => m.Definition == parameter.Owner);
        return owner is not null && parameter.Position < owner.GenericParameters.Count ? owner.GenericParameters[parameter.Position] : null;
    }

    /// <inheritdoc/>
    public TypeSymbol? EnumUnderlyingType(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = type.DefinitionOrSelf;
        if (definition.Kind != TypeSymbolKind.Named || !definition.IsValueType)
        {
            return null;
        }

        if (_shared.Declarations.TryGetValue(definition.Definition, out var declared))
        {
            return declared.BaseType is { Namespace: "System", Name: "Enum" } ? declared.Fields.FirstOrDefault(f
                => !f.IsStatic)?.FieldType : null;
        }

        return _snapshot.Catalog.Locate(definition) is { } located ? located.Source.EnumUnderlyingType(located.Handle,
            _snapshot.Catalog) : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MethodSymbol> SessionMethods => _snapshot.SessionMethods;

    /// <inheritdoc/>
    public IReadOnlyList<VariableSymbol> Locals => _snapshot.Locals;

    /// <inheritdoc/>
    public IReadOnlyList<VariableSymbol> Arguments => _snapshot.Arguments;

    /// <inheritdoc/>
    public int ThisIndex => _snapshot.ThisIndex;

    /// <inheritdoc/>
    public string Pretty(TypeSymbol? type) => SymbolRenderer.Pretty(type);

    /// <inheritdoc/>
    public string Describe(MethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return SymbolRenderer.Describe(method, Pretty);
    }

}
