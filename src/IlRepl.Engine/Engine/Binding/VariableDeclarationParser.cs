namespace IlRepl.Engine.Binding;

/// <summary>
/// Binds local and argument declarations through the same grammar for execution and preview.
/// </summary>
public static class VariableDeclarationParser
{
    /// <summary>
    /// Parses local declarations and checks their names, types and accessibility.
    /// </summary>
    /// <param name="spec">The text after .locals.</param>
    /// <param name="scope">The body scope, including existing locals.</param>
    /// <returns>The added slots.</returns>
    public static IReadOnlyList<VariableSymbol> ParseLocals(string spec, IBindingScope scope)
    {
        var text = TypeParser.Normalize(spec).Trim();
        if (text.StartsWith("init", StringComparison.Ordinal) && (text.Length == 4 || !char.IsLetterOrDigit(text[4])))
        {
            text = text[4..].Trim();
        }

        text = Unwrap(text);
        if (text.Length == 0)
        {
            throw new ReplException("usage: .locals init (int32 x, string s)");
        }

        var declared = new List<VariableSymbol>();
        foreach (var raw in CilSyntaxParser.SplitTopLevel(text))
        {
            var part = raw;
            if (part.StartsWith('['))
            {
                var close = part.IndexOf(']', StringComparison.Ordinal);
                if (close < 0)
                {
                    throw new ReplException($"bad local declaration '{raw}'");
                }

                // Only a slot index is removed; an assembly qualifier belongs to the type.
                if (int.TryParse(part.AsSpan(1, close - 1), out _))
                {
                    part = part[(close + 1)..].Trim();
                }
            }

            var position = 0;
            var bound = SymbolBinder.BindType(CilSyntaxParser.ParseTypeAt(part, ref position), scope);
            var problem = MemberEligibility.TypeVerdict(bound.ExactType, scope.Access, AccessFacts.From(scope));
            if (problem is not null)
            {
                throw new ReplException(problem);
            }

            var name = Name(part[position..], "local");
            if (name is not null && (scope.Locals.Any(v => v.Name == name) || declared.Any(v => v.Name == name)))
            {
                throw new ReplException($"local '{name}' is already declared");
            }

            if (SymbolIdentity.Equal(bound.Type, TypeSymbol.Void))
            {
                throw new ReplException("a local cannot be void");
            }

            declared.Add(new VariableSymbol(bound.Type, name, bound.Pinned)
            {
                ExactType = RuntimeSymbolTypes.RequiresExact(bound.ExactType) ? bound.ExactType : null,
            });
        }

        return declared;
    }

    /// <summary>
    /// Parses argument declarations without constructing their runtime values.
    /// </summary>
    /// <param name="spec">The text after .args.</param>
    /// <param name="scope">The body scope, including existing arguments.</param>
    /// <returns>The bound declarations and unevaluated initializers.</returns>
    public static IReadOnlyList<ArgumentSyntax> ParseArguments(string spec, IBindingScope scope)
    {
        var text = Unwrap(TypeParser.Normalize(spec).Trim());
        if (text.Length == 0)
        {
            throw new ReplException("usage: .args (int32 x = 5, string s = \"hi\")");
        }

        var declared = new List<ArgumentSyntax>();
        foreach (var part in CilSyntaxParser.SplitTopLevel(text))
        {
            var equals = DeclarationText.InitializerEquals(part);
            var declaration = equals < 0 ? part : part[..equals].Trim();
            var literal = equals < 0 ? null : part[(equals + 1)..].Trim();
            var position = 0;
            var bound = SymbolBinder.BindType(CilSyntaxParser.ParseTypeAt(declaration, ref position), scope);
            var type = bound.Type;
            var problem = MemberEligibility.TypeVerdict(bound.ExactType, scope.Access, AccessFacts.From(scope));
            if (problem is not null)
            {
                throw new ReplException(problem);
            }

            var name = Name(declaration[position..], "argument");
            if (name is not null && (scope.Arguments.Any(v => v.Name == name) || declared.Any(v => v.Name == name)))
            {
                throw new ReplException($"argument '{name}' is already declared");
            }

            if (SymbolIdentity.Equal(type, TypeSymbol.Void))
            {
                throw new ReplException("an argument cannot be void");
            }

            if (ContainsParameters(type))
            {
                throw new ReplException("arguments cannot use the cell's generic parameters; pass a concrete type");
            }

            LiteralBindingRules.Argument(literal, type, scope);

            declared.Add(new ArgumentSyntax(type, name, literal)
            {
                ExactType = RuntimeSymbolTypes.RequiresExact(bound.ExactType) ? bound.ExactType : null,
            });
        }

        return declared;
    }

    /// <summary>
    /// Determines whether a signature shape contains an unbound generic parameter.
    /// </summary>
    /// <param name="type">The signature type.</param>
    /// <returns>Whether runtime argument values cannot be supplied for the type.</returns>
    public static bool ContainsParameters(TypeSymbol type) => type.Kind switch
    {
        TypeSymbolKind.TypeParameter or TypeSymbolKind.MethodParameter => true,
        TypeSymbolKind.Named => type.GenericParameterNames.Count > 0,
        TypeSymbolKind.Constructed => type.Arguments.Any(ContainsParameters),
        TypeSymbolKind.FunctionPointer => ContainsParameters(type.Signature!.ReturnType)
            || type.Signature.Parameters.Any(ContainsParameters),
        _ => type.Element is not null && ContainsParameters(type.Element),
    };

    private static string Unwrap(string text) => text.StartsWith('(') && text.EndsWith(')') ? text[1..^1].Trim() : text;

    private static string? Name(string text, string kind)
    {
        var name = InstructionParser.Unquote(text.Trim());
        if (name.Length == 0)
        {
            return null;
        }

        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException($"bad {kind} name '{name}'");
        }

        return name;
    }
}
