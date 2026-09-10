using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Discovers eligible symbols from one editing view before spelling, confirmation or paging begins.
/// </summary>
internal sealed class CompletionCandidateSource
{
    private readonly EditingView _view;
    private readonly SnapshotBindingScope _scope;
    private TypeIndex? _index;
    private readonly CompletionSite _site;
    private readonly string? _assemblyHint;
    private readonly TypeSyntax? _typeSyntax;
    private readonly string _retainedTypeSuffix = "";
    private TypeSpeller? _typeSpeller;
    private readonly List<OperandCandidate> _candidates = [];

    private TypeIndex Index => _index ??= new TypeIndex(_view.Snapshot);

    /// <summary>
    /// Initializes discovery for one syntax-defined operand site.
    /// </summary>
    /// <param name="view">The captured editing context.</param>
    /// <param name="site">The current operand site.</param>
    /// <param name="line">The current line, including an explicit assembly qualifier on a type component.</param>
    public CompletionCandidateSource(EditingView view, CompletionSite site, string line)
    {
        _view = view;
        _scope = ((SnapshotBindingScope)view.Scope).ForConfirmation();
        _site = site;
        if (site.Kind is CompletionSiteKind.Type or CompletionSiteKind.MemberHead or CompletionSiteKind.TypeArgument)
        {
            var prefix = line.AsSpan(site.ReplaceStart).TrimStart();
            while (prefix.StartsWith("class ", StringComparison.Ordinal) || prefix.StartsWith("valuetype ", StringComparison.Ordinal))
            {
                prefix = prefix[(prefix.IndexOf(' ') + 1)..].TrimStart();
            }

            if (prefix.StartsWith("[", StringComparison.Ordinal) && prefix.IndexOf(']') is var close && close >= 0)
            {
                _assemblyHint = prefix[1..close].Trim().ToString();
            }

            try
            {
                var comment = view.InBlockComment;
                var characters = line.ToCharArray();
                foreach (var segment in CilLexer.Segments(line, ref comment))
                {
                    if (segment.Kind is CilSegmentKind.BlockComment or CilSegmentKind.LineComment)
                    {
                        characters.AsSpan(segment.Start, segment.Length).Fill(' ');
                    }
                }

                var syntaxLine = new string(characters);
                var position = site.ReplaceStart;
                var parsed = CilSyntaxParser.ParseTypeAt(syntaxLine, ref position);
                if (site.Kind == CompletionSiteKind.TypeArgument && parsed.End > site.ReplaceEnd)
                {
                    _retainedTypeSuffix = syntaxLine[site.ReplaceEnd..parsed.End];
                }

                while (parsed.Element is not null)
                {
                    parsed = parsed.Element;
                }

                _typeSyntax = parsed;
                _assemblyHint = _typeSyntax.AssemblyHint;
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // An unfinished construction still retains its complete assembly qualifier.
            }
        }
    }

    /// <summary>
    /// Warms full type details only when discovery needs a broad name search.
    /// </summary>
    /// <param name="cancellationToken">Cancels metadata work before discovery.</param>
    /// <returns>A task that settles when the required indexes are ready.</returns>
    public async ValueTask PrepareAsync(CancellationToken cancellationToken)
    {
        if (_site.Kind is CompletionSiteKind.Method or CompletionSiteKind.Constructor or CompletionSiteKind.Field)
        {
            if (string.IsNullOrWhiteSpace(_site.DeclaringTypeText))
            {
                return;
            }

            try
            {
                if (ExactOwner(CilSyntaxParser.ParseType(_site.DeclaringTypeText)) is not null)
                {
                    return;
                }
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // Case correction still searches every captured type when the exact qualifier does not bind.
            }
        }
        else if (_site.Kind is not (CompletionSiteKind.Type or CompletionSiteKind.MemberHead
            or CompletionSiteKind.TypeArgument or CompletionSiteKind.Signature))
        {
            return;
        }

        foreach (var source in _view.Snapshot.Catalog.Sources)
        {
            await source.Index.WarmEntriesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Collects every eligible matching symbol while yielding during broad type searches.
    /// </summary>
    /// <param name="genericArguments">The generic argument owners selected by syntax and valid anchors.</param>
    /// <param name="genericMethods">The applicable constructed methods at a signature site.</param>
    /// <param name="labels">The labels in this body's complete document segment.</param>
    /// <param name="hasElementSuffix">Whether the type is followed by an existing element suffix.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The full candidate set, still unconfirmed and unranked.</returns>
    public async ValueTask<IReadOnlyList<OperandCandidate>> GatherAsync(
        IReadOnlyList<GenericArgumentContext> genericArguments, IReadOnlyList<MethodSymbol> genericMethods,
        IReadOnlySet<string> labels, bool hasElementSuffix, CancellationToken cancellationToken)
    {
        switch (_site.Kind)
        {
            case CompletionSiteKind.Type:
            case CompletionSiteKind.MemberHead:
            case CompletionSiteKind.TypeArgument:
                await AddTypesAsync(genericArguments, hasElementSuffix, cancellationToken);
                if (_site.Kind == CompletionSiteKind.MemberHead && !_site.NextIsDoubleColon
                    && _site.Owner is "call" or "jmp" or "ldftn" or "ldtoken method" or ".dis" or ".disassemble")
                {
                    foreach (var method in _scope.SessionMethods)
                    {
                        AddMethod(method);
                    }
                }

                break;
            case CompletionSiteKind.Method:
            case CompletionSiteKind.Constructor:
            case CompletionSiteKind.Field:
                foreach (var owner in ResolveOwners(_site.DeclaringTypeText))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_site.Kind == CompletionSiteKind.Field)
                    {
                        foreach (var field in _scope.Fields(owner))
                        {
                            if (MemberEligibility.Admits(field, _site, _view))
                            {
                                _candidates.Add(new OperandCandidate
                                {
                                    Kind = CompletionKind.Fields, Field = field,
                                    Rank = new CandidateRankFacts(field.Name, field.Name, _scope.IsSessionType(field.DeclaringType),
                                        IsCompilerGenerated: field.Name.StartsWith('<'),
                                        DeclaringPath: SymbolRenderer.IlPath(field.DeclaringType)),
                                });
                            }
                        }
                    }
                    else
                    {
                        var methods = _scope.AllMethods(owner).Concat(_scope.Constructors(owner, false));
                        if (_site.Owner is "ldtoken method" or ".dis" or ".disassemble")
                        {
                            methods = methods.Concat(_scope.Constructors(owner, true));
                        }

                        foreach (var method in methods)
                        {
                            AddMethod(method);
                        }
                    }

                    await Task.Yield();
                }

                break;
            case CompletionSiteKind.Signature:
                foreach (var method in genericMethods)
                {
                    AddMethod(method);
                }

                break;
            case CompletionSiteKind.Local:
            case CompletionSiteKind.Argument:
            {
                var locals = _site.Kind == CompletionSiteKind.Local;
                var variables = locals ? _scope.Locals : _scope.Arguments;
                for (var i = 0; i < variables.Count; i++)
                {
                    var name = variables[i].Name ?? (!locals && i == _scope.ThisIndex ? "this" : SymbolRenderer.Number(i));
                    _candidates.Add(new OperandCandidate
                    {
                        Kind = locals ? CompletionKind.Locals : CompletionKind.Arguments,
                        Slot = i, Type = variables[i].Type, Rank = new CandidateRankFacts(name, name, IsSession: true),
                    });
                }

                break;
            }
            case CompletionSiteKind.Label:
                foreach (var label in labels.Order(StringComparer.Ordinal))
                {
                    _candidates.Add(new OperandCandidate
                    {
                        Kind = CompletionKind.Labels, Rank = new CandidateRankFacts(label, label, IsSession: true),
                    });
                }

                break;
            case CompletionSiteKind.GenericParameter:
                foreach (var parameter in _scope.Generics.TypeArguments.Concat(_scope.Generics.MethodArguments))
                {
                    AddType(parameter, SymbolRenderer.Pretty(parameter), false, [], hasElementSuffix);
                }

                break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _candidates;
    }

    /// <summary>
    /// Resolves a typed qualifier or corrects its case while retaining any explicit assembly and generic arguments.
    /// </summary>
    /// <param name="text">The qualifier as typed.</param>
    /// <param name="genericDefinitionsOnly">Whether an open type-argument list requires all matching generic definitions.</param>
    /// <returns>The exact matching declaring constructions.</returns>
    public IReadOnlyList<TypeSymbol> ResolveOwners(string? text, bool genericDefinitionsOnly = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return _view.Owner is not { } owner ? [] : _site.Owner == ".override"
                ? [owner, .. SymbolRelations.AllInterfacesOf(owner, _scope)] : [owner];
        }

        TypeSyntax syntax;
        try
        {
            syntax = CilSyntaxParser.ParseType(text);
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            return [];
        }

        var hinted = syntax.AssemblyHint;
        if (hinted is not null && hinted != "ilrepl" && _view.Snapshot.Catalog.FindAssembly(hinted) is null)
        {
            return [];
        }

        if (!genericDefinitionsOnly && ExactOwner(syntax) is { } exact)
        {
            return [exact];
        }

        if (syntax.Kind != TypeSyntaxKind.Named)
        {
            return [];
        }

        var matches = Index.Entries.Where(entry =>
            string.Equals(entry.IlPath, syntax.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(WithoutArity(entry.IlPath), syntax.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            var separator = Math.Max(syntax.Name!.LastIndexOf('.'), syntax.Name.LastIndexOf('/'));
            var name = syntax.Name[(separator + 1)..];
            matches = Index.Entries.Where(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.BareName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (separator < 0 && hinted is null)
            {
                matches = matches.Where(entry => Index.ShortNameTarget(entry.Name) is not { } preferred
                    || SymbolIdentity.Equal(Index.SymbolOf(entry), preferred)).ToArray();
            }
        }

        var result = new List<TypeSymbol>();
        foreach (var entry in matches)
        {
            try
            {
                if (Index.SymbolOf(entry) is not { } type || !HintNames(hinted, type)
                    || genericDefinitionsOnly && !type.IsGenericDefinition)
                {
                    continue;
                }

                var corrected = syntax with { Name = SymbolRenderer.IlPath(type), AssemblyHint = entry.AssemblyName };
                var found = SymbolBinder.BindType(corrected, _scope).Type;
                if (SymbolIdentity.Equal(found.DefinitionOrSelf, type.DefinitionOrSelf)
                    && !result.Any(existing => SymbolIdentity.Equal(existing, found)))
                {
                    result.Add(found);
                }
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // A rejected construction or unspellable collision does not remove another valid owner.
            }
        }

        return result;
    }

    private TypeSymbol? ExactOwner(TypeSyntax syntax)
    {
        try
        {
            var found = SymbolBinder.BindType(syntax, _scope).Type;
            return HintNames(syntax.AssemblyHint, found) ? found : null;
        }
        catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
        {
            return null;
        }
    }

    private async ValueTask AddTypesAsync(
        IReadOnlyList<GenericArgumentContext> arguments, bool suffix, CancellationToken cancellationToken)
    {
        var seen = new HashSet<TypeSymbol>();
        var count = 0;
        var query = DecodePrefix(_site.Prefix);
        var qualified = query.Contains('.') || query.Contains('/');
        var hasArity = query.LastIndexOf('`') > Math.Max(query.LastIndexOf('/'), query.LastIndexOf('.'));
        foreach (var entry in Index.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count % 128 == 0)
            {
                await Task.Yield();
            }

            var matching = qualified ? entry.IlPath : entry.Name;
            if (!hasArity)
            {
                matching = WithoutArity(matching);
            }
            if (CandidateRanker.Match(query, matching).Tier == MatchTier.None)
            {
                continue;
            }

            try
            {
                if (Index.SymbolOf(entry) is { } type && HintNames(_assemblyHint, type) && seen.Add(type))
                {
                    AddType(type, matching, entry.IsCompilerGenerated, arguments, suffix);
                }
            }
            catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
            {
                // Metadata failure is confined to the candidate being inspected.
            }
        }

        foreach (var type in CilPrimitives.Keywords.Select(TypeSymbol.Primitive)
            .Concat(_scope.Generics.TypeArguments).Concat(_scope.Generics.MethodArguments))
        {
            if (HintNames(_assemblyHint, type) && seen.Add(type))
            {
                AddType(type, SymbolRenderer.Pretty(type), false, arguments, suffix);
            }
        }
    }

    private void AddType(
        TypeSymbol type, string matching, bool generated, IReadOnlyList<GenericArgumentContext> arguments, bool suffix)
    {
        if (_typeSyntax is { HasArguments: true } existing && existing.End <= _site.ReplaceEnd)
        {
            if (!type.IsGenericDefinition || type.Arity != existing.Arguments.Count)
            {
                return;
            }

            var corrected = existing with { Name = SymbolRenderer.IlPath(type), AssemblyHint = type.AssemblyName };
            var constructed = SymbolBinder.BindType(corrected, _scope).Type;
            if (!SymbolIdentity.Equal(constructed.DefinitionOrSelf, type))
            {
                return;
            }

            type = constructed;
        }

        if (!MemberEligibility.Admits(type, _site, _view, suffix))
        {
            return;
        }

        var kind = type.IsGenericParameter ? CompletionKind.GenericParameters : CompletionKind.Types;
        var label = SymbolRenderer.Pretty(type);
        if (type.Keyword is { } keyword)
        {
            var query = DecodePrefix(_site.Prefix);
            var alias = CandidateRanker.Match(query, keyword);
            var metadata = CandidateRanker.Match(query, matching);
            if (alias.Tier < metadata.Tier || alias.Tier == metadata.Tier && alias.ExactCase && !metadata.ExactCase)
            {
                matching = keyword;
            }
        }

        var candidate = new OperandCandidate
        {
            Kind = kind, Type = type,
            Rank = new CandidateRankFacts(matching, label, _scope.IsSessionType(type),
                TypePreference(type), generated, DeclaringPath: SymbolRenderer.IlPath(type)),
        };
        if (_site.Kind == CompletionSiteKind.TypeArgument)
        {
            var supplied = type;
            if (!type.IsGenericDefinition && _retainedTypeSuffix.Length > 0)
            {
                _typeSpeller ??= new TypeSpeller(_scope);
                supplied = SymbolBinder.BindType(CilSyntaxParser.ParseType(_typeSpeller.Spell(type) + _retainedTypeSuffix), _scope).Type;
            }

            foreach (var argument in arguments)
            {
                if (!type.IsGenericDefinition && argument.Allows(supplied, _scope))
                {
                    _candidates.Add(candidate with { Kind = CompletionKind.TypeArguments, GenericOwner = argument.Target });
                }
                else if (type.IsGenericDefinition)
                {
                    _candidates.Add(candidate with
                    {
                        Kind = CompletionKind.TypeArguments, StartsGeneric = true, GenericOwner = argument.Target,
                    });
                }
            }
        }
        else if (type.IsGenericDefinition)
        {
            _candidates.Add(candidate with { Kind = CompletionKind.TypeArguments, StartsGeneric = true });
            if (_site.Owner is "ldtoken" or "ldtoken method" or ".dis" or ".disassemble")
            {
                _candidates.Add(candidate);
            }
        }
        else
        {
            _candidates.Add(candidate);
        }
    }

    private void AddMethod(MethodSymbol method)
    {
        if (!MemberEligibility.Admits(method, _site, _view))
        {
            return;
        }

        var candidate = new OperandCandidate
        {
            Kind = method.Source == MethodSymbolSource.Session ? CompletionKind.Methods : CompletionKind.Members,
            Method = method,
            Rank = new CandidateRankFacts(method.Name, method.Name,
                method.Source == MethodSymbolSource.Session || method.DeclaringType is { } owner && _scope.IsSessionType(owner),
                IsCompilerGenerated: method.Name.StartsWith('<'), ParameterCount: method.Parameters.Count,
                ParameterList: string.Join(", ", method.ParameterTypes.Select(SymbolRenderer.Pretty)),
                DeclaringPath: method.DeclaringType is null ? "" : SymbolRenderer.IlPath(method.DeclaringType)),
        };
        if (method.IsGenericDefinition)
        {
            _candidates.Add(candidate with { Kind = CompletionKind.TypeArguments, StartsGeneric = true });
            if (_site.Owner is "ldtoken method" or ".dis" or ".disassemble")
            {
                _candidates.Add(candidate);
            }
        }
        else
        {
            _candidates.Add(candidate);
        }
    }

    private int TypePreference(TypeSymbol type) => _site.Owner switch
    {
        "box" or "initobj" or "sizeof" => type.IsValueTypeShape ? 0 : 1,
        "castclass" or "isinst" or "catch" => type.IsValueTypeShape ? 1 : 0,
        _ => 0,
    };

    private bool HintNames(string? hint, TypeSymbol type) => _scope.AssemblyHintNamesType(hint, type);

    /// <summary>
    /// Decodes quoted matching text without treating generic punctuation inside a quoted name as syntax.
    /// </summary>
    /// <param name="text">The prefix supplied by the classifier.</param>
    /// <returns>The decoded matching prefix.</returns>
    public static string DecodePrefix(string text)
    {
        var result = new System.Text.StringBuilder();
        var index = 0;
        if (text.StartsWith('[') && text.IndexOf(']', StringComparison.Ordinal) is var end && end >= 0)
        {
            index = end + 1;
        }

        while (index < text.Length)
        {
            if (text[index] != '\'')
            {
                result.Append(text[index++]);
                continue;
            }

            var start = ++index;
            while (index < text.Length && text[index] != '\'')
            {
                index += text[index] == '\\' && index + 1 < text.Length ? 2 : 1;
            }

            result.Append(TypeParser.DecodeQuoted(text[start..index]));
            if (index < text.Length)
            {
                index++;
            }
        }

        return result.ToString();
    }

    private static string WithoutArity(string path)
    {
        var tick = path.LastIndexOf('`');
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('.'));
        return tick <= separator || tick == path.Length - 1 || path.AsSpan(tick + 1).IndexOfAnyExceptInRange('0', '9') >= 0
            ? path : path[..tick];
    }
}
