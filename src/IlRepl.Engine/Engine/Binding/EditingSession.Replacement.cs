namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    private Dictionary<string, DeclarationSymbol>? _predeclared;
    private Dictionary<string, MethodSymbol>? _preparedMethods;
    private List<TypeValidationState>? _pendingTypeValidations;
    private List<EditingBody>? _pendingBodyValidations;

    private bool ReplaceFamily(EditingTypeBlock candidate)
    {
        if (_predeclared is not null)
        {
            return false;
        }

        var previous = candidate.Before.Definitions.FirstOrDefault(
            definition => definition.IsFamily && definition.Name == candidate.Path);
        if (previous is null)
        {
            return false;
        }

        EditingDefinition[] families = [.. candidate.Before.Definitions.Where(definition => definition.IsFamily)];
        EditingDefinition[] methods = [.. candidate.Before.Definitions.Where(definition => !definition.IsFamily)];
        var closure = DefinitionReplacementPlanner.Plan(
            previous, families, methods, definition => definition.Identities,
            Mentions, Mentions, definition => definition.Name, EqualityComparer<TypeSymbol>.Default);
        var originalScope = Scope();
        var definitions = new Dictionary<string, DeclarationSymbol>(StringComparer.Ordinal);
        foreach (var entry in _state.Types.Entries.Where(entry => IsFamilyPath(entry.FullName, candidate.Path)))
        {
            definitions[entry.FullName] = ReadDeclaration(entry.Type, originalScope);
        }

        foreach (var dependent in closure.Families)
        {
            foreach (var entry in candidate.Before.CommittedTypes.Entries.Where(entry => IsFamilyPath(entry.FullName, dependent.Name)))
            {
                definitions[entry.FullName] = ReadDeclaration(entry.Type, originalScope);
            }
        }

        var replacements = new Dictionary<TypeSymbol, TypeSymbol>();
        var owners = new Dictionary<DefinitionId, DefinitionId>();
        foreach (var (path, declaration) in definitions.OrderBy(pair => pair.Key.Count(character => character == '/')))
        {
            var old = declaration.Type;
            var enclosing = old.Declaring is null ? null
                : replacements.GetValueOrDefault(old.Declaring, old.Declaring);
            var replacement = IsFamilyPath(path, candidate.Path) ? old : TypeSymbol.Named(
                NextDefinition(), old.Name, old.Namespace, enclosing, "ilrepl", old.Attributes, old.IsValueTypeShape,
                old.GenericParameterNames, old.IsByRefLike);
            replacements[old] = replacement;
            owners[old.Definition] = replacement.Definition;
        }

        foreach (var family in closure.Families.Prepend(previous))
        {
            foreach (var identity in family.Identities)
            {
                var path = SymbolRenderer.IlPath(identity);
                if (!definitions.TryGetValue(path, out var declaration))
                {
                    if (closure.Families.Concat(closure.Methods).Any(dependent =>
                        dependent.ReferencedTypes.Any(type => MentionsType(type, new HashSet<TypeSymbol> { identity })))
                        || ReferencedTypes(_state.Cell).Any(type => MentionsType(type, new HashSet<TypeSymbol> { identity })))
                    {
                        throw new ReplException($"cannot redefine {candidate.Path}: {path} is still referenced by a dependent");
                    }

                    continue;
                }

                var replacement = replacements[declaration.Type];
                replacements[identity] = replacement;
                owners[identity.Definition] = replacement.Definition;
            }
        }

        TypeSymbol Map(TypeSymbol type) => SymbolRelations.Rewrite(type, symbol =>
        {
            if (replacements.TryGetValue(symbol, out var replacement))
            {
                return replacement;
            }

            return symbol.Kind == TypeSymbolKind.TypeParameter && owners.TryGetValue(symbol.Owner, out var owner)
                ? TypeSymbol.Parameter(owner, false, symbol.Position, symbol.Name, symbol.ParameterAttributes) : null;
        });

        _predeclared = new Dictionary<string, DeclarationSymbol>(StringComparer.Ordinal);
        _preparedMethods = new Dictionary<string, MethodSymbol>(StringComparer.Ordinal);
        _pendingTypeValidations = [];
        _pendingBodyValidations = [];
        try
        {
            foreach (var (path, declaration) in definitions)
            {
                var replacement = replacements[declaration.Type];
                var shape = new DeclarationSymbol(
                    replacement, declaration.BaseType is null ? null : Map(declaration.BaseType),
                    [.. declaration.Interfaces.Select(Map)],
                    [.. declaration.GenericParameters.Select(parameter => parameter with
                    {
                        Owner = replacement.Definition, Constraints = [.. parameter.Constraints.Select(Map)],
                    })],
                    declaration.Fields.Select(field => SymbolRemapper.Field(field, NextDefinition(), Map)),
                    declaration.Methods.Select(method => SymbolRemapper.Method(method, NextDefinition(), Map, false)), true)
                {
                    Properties = [.. declaration.Properties.Select(property => property with
                    {
                        Definition = NextDefinition(), DeclaringType = replacement, Type = Map(property.Type),
                        Parameters = [.. property.Parameters.Select(Map)],
                    })],
                };
                _predeclared.Add(path, shape);
                _state.Types.Add(path, replacement, shape.Clone());
            }

            _state.OpenTypes.Clear();
            foreach (var dependent in closure.Methods)
            {
                var signature = _state.Methods.Single(method => method.Name == dependent.Name);
                var replacement = SymbolRemapper.Method(signature, NextDefinition(), Map, true);
                _preparedMethods.Add(dependent.Name, replacement);
                _state.Methods[_state.Methods.IndexOf(signature)] = replacement;
            }

            foreach (var family in closure.Families)
            {
                ReplayDefinition(family.Header, family.Lines);
            }

            ReplayDefinition(candidate.HeaderLine, candidate.Lines);
            foreach (var dependent in closure.Methods)
            {
                ReplayDefinition(dependent.Header, dependent.Lines);
            }

            foreach (var declaration in _pendingTypeValidations)
            {
                TypeDeclarationBinding.Validate(declaration, Scope());
            }

            foreach (var body in _pendingBodyValidations)
            {
                RecheckBody(body);
            }

            string[] declarations = [.. _state.CellDeclarations];
            string[] lines = [.. _state.Cell.Lines.Where(line => !IsCellDeclaration(line))];
            var labelSpace = _state.Cell.LabelSpace;
            _state.Cell = new EditingBody { LabelSpace = labelSpace };
            _state.CellDeclarations.Clear();
            foreach (var line in declarations.Concat(lines))
            {
                AddLine(line);
            }

            if (_state.TypeArguments is { } arguments)
            {
                _state.TypeArguments = [.. arguments.Select(Map)];
            }

            _state.CommittedTypes = _state.Types.Clone();
            return true;
        }
        finally
        {
            _predeclared = null;
            _preparedMethods = null;
            _pendingTypeValidations = null;
            _pendingBodyValidations = null;
        }
    }

    private void ReplayDefinition(string header, IReadOnlyList<string> lines)
    {
        AddLine(header);
        foreach (var line in lines)
        {
            AddLine(line);
        }

        if (_state.Method is not null || _state.OpenTypes.Count > 0)
        {
            AddLine("}");
        }
    }

    private static DeclarationSymbol ReadDeclaration(TypeSymbol type, SnapshotBindingScope scope)
    {
        if (scope.Snapshot.Types.DeclarationOf(type) is { } declaration)
        {
            return declaration.Clone();
        }

        return new DeclarationSymbol(type, scope.BaseOf(type), scope.DeclaredInterfacesOf(type),
            scope.GenericParameterDeclarations(type),
            scope.Fields(type).Where(field => SymbolIdentity.Equal(field.DeclaringType, type)),
            scope.AllMethods(type).Concat(scope.Constructors(type, false)).Concat(scope.Constructors(type, true))
                .Where(method => SymbolIdentity.Equal(method.DeclaringType, type)), false)
        {
            Properties = [.. scope.Properties(type).Where(property => SymbolIdentity.Equal(property.DeclaringType, type))],
        };
    }

    private static bool Mentions(EditingDefinition definition, IReadOnlySet<TypeSymbol> types, IReadOnlySet<string> methods) =>
        definition.ReferencedMethods.Overlaps(methods) || definition.ReferencedTypes.Any(type => MentionsType(type, types));

    private static bool MentionsType(TypeSymbol type, IReadOnlySet<TypeSymbol> types)
    {
        var mentioned = false;
        SymbolRelations.Rewrite(type, node =>
        {
            mentioned |= types.Contains(node);
            return null;
        });
        return mentioned;
    }

    private static bool IsFamilyPath(string path, string family) => path == family
        || path.StartsWith(family + "/", StringComparison.Ordinal);

    private static bool IsCellDeclaration(string text) => IsDirective(text, ".locals") || IsDirective(text, ".args")
        || IsDirective(text, ".typeparams") || IsDirective(text, ".vararg");
}
