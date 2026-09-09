using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Produces the shortest type spelling that binds to the selected identity in a captured context.
/// </summary>
public sealed class TypeSpeller
{
    private readonly SnapshotBindingScope _scope;
    private readonly Dictionary<TypeSymbol, string?> _spellings = [];

    /// <summary>
    /// Initializes a speller whose confirmations cannot create forward declarations or invoke runtime resolution.
    /// </summary>
    /// <param name="scope">The captured binding context.</param>
    public TypeSpeller(SnapshotBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope = scope.ForConfirmation();
    }

    /// <summary>
    /// Finds a complete spelling, or returns null when this context cannot name the exact type.
    /// </summary>
    /// <param name="type">The intended type.</param>
    /// <returns>The confirmed spelling, or null.</returns>
    public string? TrySpell(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (_spellings.TryGetValue(type, out var cached))
        {
            return cached;
        }

        string? spelling = null;
        try
        {
            if (!type.HasUnresolved)
            {
                spelling = Candidates(type).Distinct(StringComparer.Ordinal).FirstOrDefault(candidate => Binds(candidate, type));
            }
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            // One unreadable signature does not hide the other candidates in its assembly.
        }

        _spellings[type] = spelling;
        return spelling;
    }

    /// <summary>
    /// Spells the exact type or reports that this context cannot name it.
    /// </summary>
    /// <param name="type">The intended type.</param>
    /// <returns>The confirmed spelling.</returns>
    public string Spell(TypeSymbol type) => TrySpell(type)
        ?? throw new ReplException($"{SymbolRenderer.Pretty(type)} has no unambiguous spelling in this context");

    /// <summary>
    /// Spells a selected generic definition through its opening angle bracket without inventing argument types.
    /// </summary>
    /// <param name="type">The selected generic definition.</param>
    /// <returns>The confirmed definition starter, or null.</returns>
    public string? GenericStarter(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsGenericDefinition)
        {
            return null;
        }

        var tick = type.Name.LastIndexOf('`');
        var bare = tick < 0 ? type.Name : type.Name[..tick];
        var shortName = TypeNameFormatter.IlAsmTypeName(bare);
        foreach (var candidate in new[] { shortName }.Concat(DefinitionNames(type)))
        {
            try
            {
                var syntax = CilSyntaxParser.ParseType(candidate);
                var found = _scope.LookupType(syntax.Name!, syntax.AssemblyHint, type.Arity, type.IsValueTypeShape).Type;
                if (SymbolIdentity.Equal(found, type))
                {
                    return candidate + "<";
                }
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // An explicit path or arity can distinguish definitions sharing the short name.
            }
        }

        return null;
    }

    /// <summary>
    /// Quotes each namespace and nesting segment without changing its metadata spelling.
    /// </summary>
    /// <param name="type">The named definition or construction.</param>
    /// <returns>The namespace-qualified IL path.</returns>
    public static string QualifiedPath(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = type.DefinitionOrSelf;
        if (definition.Kind == TypeSymbolKind.Primitive)
        {
            return CilPrimitives.CoreLibNameOf(definition.Keyword!);
        }

        var name = TypeNameFormatter.IlAsmTypeName(definition.Name);
        if (definition.Declaring is not null)
        {
            return QualifiedPath(definition.Declaring) + "/" + name;
        }

        return definition.Namespace.Length == 0 ? name
            : string.Join(".", definition.Namespace.Split('.').Select(TypeNameFormatter.IlAsmIdentifier)) + "." + name;
    }

    private IEnumerable<string> Candidates(TypeSymbol type)
    {
        switch (type.Kind)
        {
            case TypeSymbolKind.Primitive:
                yield return type.Keyword!;
                break;
            case TypeSymbolKind.TypeParameter:
            case TypeSymbolKind.MethodParameter:
                yield return (type.Kind == TypeSymbolKind.MethodParameter ? "!!" : "!") + SymbolRenderer.Number(type.Position);
                break;
            case TypeSymbolKind.Named:
                foreach (var candidate in DefinitionNames(type))
                {
                    yield return candidate;
                }

                break;
            case TypeSymbolKind.Constructed:
            {
                var arguments = "<" + string.Join(", ", type.Arguments.Select(Spell)) + ">";
                foreach (var candidate in DefinitionNames(type.Element!))
                {
                    yield return candidate + arguments;
                }

                break;
            }
            case TypeSymbolKind.SzArray:
                yield return Spell(type.Element!) + "[]";
                break;
            case TypeSymbolKind.Array:
                yield return Spell(type.Element!) + (type.Rank == 1 ? "[0...]" : "[" + new string(',', type.Rank - 1) + "]");
                break;
            case TypeSymbolKind.ByRef:
                yield return Spell(type.Element!) + "&";
                break;
            case TypeSymbolKind.Pointer:
                yield return Spell(type.Element!) + "*";
                break;
            case TypeSymbolKind.Pinned:
                yield return Spell(type.Element!) + " pinned";
                break;
            case TypeSymbolKind.Modified:
                yield return Spell(type.Element!) + (type.IsRequired ? " modreq(" : " modopt(") + Spell(type.Modifier!) + ")";
                break;
            case TypeSymbolKind.FunctionPointer:
            {
                var signature = type.Signature!;
                var returnType = Spell(signature.ReturnType);
                var text = SymbolRenderer.Signature(signature, candidate => Spell(candidate!));
                var conventionLength = text.IndexOf(returnType, StringComparison.Ordinal);
                yield return "method " + text[..(conventionLength + returnType.Length)] + " *"
                    + text[(conventionLength + returnType.Length)..];
                break;
            }
        }
    }

    private IEnumerable<string> DefinitionNames(TypeSymbol type)
    {
        yield return TypeNameFormatter.IlAsmTypeName(type.Name);
        if (type.Declaring is not null)
        {
            yield return NestedPath(type);
        }

        var qualified = QualifiedPath(type);
        yield return qualified;
        if (_scope.IsSessionType(type))
        {
            yield return "[ilrepl]" + qualified;
        }
        else if (type.AssemblyName.Length > 0)
        {
            yield return "[" + type.AssemblyName + "]" + qualified;
        }
    }

    private static string NestedPath(TypeSymbol type) => type.Declaring is null
        ? TypeNameFormatter.IlAsmTypeName(type.Name)
        : NestedPath(type.Declaring) + "/" + TypeNameFormatter.IlAsmTypeName(type.Name);

    private bool Binds(string text, TypeSymbol target)
    {
        try
        {
            var bound = SymbolBinder.BindType(CilSyntaxParser.ParseType(text), _scope);
            var core = target.Unwrapped;
            var required = new List<TypeSymbol>();
            var optional = new List<TypeSymbol>();
            var pinned = false;
            for (var current = target; current.Kind is TypeSymbolKind.Modified or TypeSymbolKind.Pinned; current = current.Element!)
            {
                pinned |= current.Kind == TypeSymbolKind.Pinned;
                if (current.Kind == TypeSymbolKind.Modified)
                {
                    (current.IsRequired ? required : optional).Insert(0, current.Modifier!);
                }
            }

            return SymbolIdentity.Equal(bound.Type, core) && bound.Pinned == pinned
                && SymbolIdentity.SequenceEqual(bound.RequiredModifiers, required)
                && SymbolIdentity.SequenceEqual(bound.OptionalModifiers, optional);
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            return false;
        }
    }
}
