using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Parses session and class method headers through the shared symbol binder.
/// </summary>
public static class MethodDeclarationParser
{
    private const string Usage = "usage: .method <return type> Name(<T name>, ...) {  (use void for no return value)";
    private const string MemberUsage = "usage: .method [public] [static] [virtual] RetType Name(<T name>, ...) {";

    private static readonly string[] Modifiers =
        ["public", "private", "assembly", "family", "famandassem", "famorassem", "static", "hidebysig", "specialname", "rtspecialname"];

    private static readonly string[] Trailers = ["cil", "managed", "il", "noinlining", "aggressiveinlining", "synchronized"];

    private static readonly Dictionary<string, MethodImplAttributes> ImplWords = new(StringComparer.Ordinal)
    {
        ["cil"] = MethodImplAttributes.IL, ["il"] = MethodImplAttributes.IL, ["managed"] = MethodImplAttributes.Managed,
        ["noinlining"] = MethodImplAttributes.NoInlining, ["aggressiveinlining"] = MethodImplAttributes.AggressiveInlining,
        ["synchronized"] = MethodImplAttributes.Synchronized, ["nooptimization"] = MethodImplAttributes.NoOptimization,
        ["aggressiveoptimization"] = MethodImplAttributes.AggressiveOptimization,
    };

    /// <summary>
    /// Parses an instance, static, constructor or generic member header with its ILAsm attributes.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="context">The parse context of the open type, with its generic parameters in scope.</param>
    /// <param name="owner">The type being written.</param>
    /// <param name="opensBlock">True when the header ended with <c>{</c>.</param>
    /// <param name="closesBlock">True when the header ended with <c>{ }</c>.</param>
    /// <param name="typeParameters">The generic parameters of a generic method, declared so <c>!!N</c> resolves while parsing.</param>
    /// <param name="defineTypeParameters">Declares parameters in the caller's runtime or symbolic state.</param>
    /// <returns>The signature with its attributes.</returns>
    /// <exception cref="ReplException">The header is malformed or inconsistent.</exception>
    public static MethodSymbol ParseMember(
        string spec,
        IBindingScope context,
        TypeHeader owner,
        out bool opensBlock,
        out bool closesBlock,
        out TypeSymbol[] typeParameters,
        Func<string[], TypeSymbol[]> defineTypeParameters)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(owner);
        var s = TypeParser.Normalize(spec).Trim();
        opensBlock = false;
        closesBlock = false;
        if (s.EndsWith('}'))
        {
            var brace = s.LastIndexOf('{');
            if (brace < 0 || s[(brace + 1)..^1].Trim().Length > 0)
            {
                throw new ReplException("a .method header may end with { or { }, nothing else");
            }

            opensBlock = true;
            closesBlock = true;
            s = s[..brace].TrimEnd();
        }
        else if (s.EndsWith('{'))
        {
            opensBlock = true;
            s = s[..^1].TrimEnd();
        }

        var attributes = MethodAttributes.PrivateScope;
        var accessSeen = false;
        var isStatic = false;
        var isInstance = false;
        var convention = CallingConventions.Standard;
        var pos = 0;
        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            var word = PeekWord(s, pos);
            MethodAttributes? access = word switch
            {
                "public" => MethodAttributes.Public,
                "private" => MethodAttributes.Private,
                "family" => MethodAttributes.Family,
                "assembly" => MethodAttributes.Assembly,
                "famandassem" => MethodAttributes.FamANDAssem,
                "famorassem" => MethodAttributes.FamORAssem,
                "privatescope" => MethodAttributes.PrivateScope,
                _ => null,
            };
            if (access is { } a)
            {
                if (accessSeen)
                {
                    throw new ReplException("a method has one access word");
                }

                accessSeen = true;
                attributes = (attributes & ~MethodAttributes.MemberAccessMask) | a;
                pos += word.Length;
                continue;
            }

            switch (word)
            {
                case "static":
                    isStatic = true;
                    break;
                case "instance":
                    isInstance = true;
                    break;
                case "virtual":
                    attributes |= MethodAttributes.Virtual;
                    break;
                case "newslot":
                    attributes |= MethodAttributes.NewSlot;
                    break;
                case "final":
                    attributes |= MethodAttributes.Final;
                    break;
                case "abstract":
                    attributes |= MethodAttributes.Abstract;
                    break;
                case "hidebysig":
                    attributes |= MethodAttributes.HideBySig;
                    break;
                case "specialname":
                    attributes |= MethodAttributes.SpecialName;
                    break;
                case "rtspecialname":
                    attributes |= MethodAttributes.RTSpecialName;
                    break;
                case "strict":
                    attributes |= MethodAttributes.CheckAccessOnOverride;
                    break;
                case "vararg":
                    convention = CallingConventions.VarArgs;
                    break;
                case "default":
                    break;
                case "explicit":
                    throw new ReplException("'explicit' this is not supported on session methods");
                case "pinvokeimpl":
                case "unmanagedexp":
                case "reqsecobj":
                case "flags":
                    throw new ReplException($"'{word}' is not supported on session methods");
                default:
                    word = "";
                    break;
            }

            if (word.Length == 0)
            {
                break;
            }

            pos += word.Length;
        }

        if (isStatic && isInstance)
        {
            throw new ReplException("a method is static or instance, not both");
        }

        if (isStatic)
        {
            attributes |= MethodAttributes.Static;
        }
        else
        {
            convention |= CallingConventions.HasThis;
        }

        var firstParen = FindParameterList(s, pos);
        var beforeParen = (firstParen < 0 ? s[pos..] : s[pos..firstParen]).Trim();
        if (firstParen < 0 || beforeParen.Length == 0 || !beforeParen.Any(char.IsWhiteSpace))
        {
            throw new ReplException(MemberUsage);
        }

        // A generic method's parameters come after the name and must be in scope before the
        // return and parameter types are read, so the name is found first and the return type
        // is parsed once !!N can be resolved.
        var nameStart = FindNameStart(s, pos, firstParen);
        var nameText = s[nameStart..firstParen].Trim();
        var typeParameterSpecs = (IReadOnlyList<GenericParameterSpec>)[];
        var lt = nameText.IndexOf('<', StringComparison.Ordinal);
        if (lt >= 0)
        {
            if (!nameText.EndsWith('>'))
            {
                throw new ReplException("unbalanced '<' in method name");
            }

            typeParameterSpecs = GenericParameterParser.Parse(nameText[(lt + 1)..^1]);
            nameText = nameText[..lt].Trim();
        }

        var name = InstructionParser.Unquote(nameText);
        if (name.Length == 0)
        {
            throw new ReplException(MemberUsage);
        }

        if (name is not (".ctor" or ".cctor") && !InstructionParser.IsIdentifier(name))
        {
            throw new ReplException($"bad method name '{name}'");
        }

        var typeParameterNames = typeParameterSpecs.Select(p => p.Name).ToArray();
        typeParameters = typeParameterNames.Length == 0 ? [] : defineTypeParameters(typeParameterNames);
        var memberContext = typeParameters.Length == 0
            ? context
            : context.WithGenerics(new SymbolGenericContext(context.Generics.TypeArguments, typeParameters));
        var returnStart = pos;
        var returnType = BindTypeAt(s, ref returnStart, memberContext, out _, out var returnRequired, out var returnOptional);
        TypeParser.SkipWhitespace(s, ref returnStart);
        if (returnStart != nameStart)
        {
            throw new ReplException($"unexpected '{s[returnStart..nameStart].Trim()}' before the method name");
        }

        var close = TypeParser.FindMatchingParen(s, firstParen);
        var impl = MethodImplAttributes.IL | MethodImplAttributes.Managed;
        foreach (var word in s[(close + 1)..].Split([' ', '	'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (ImplWords.TryGetValue(word, out var flag))
            {
                impl |= flag;
            }
            else if (word is "native" or "unmanaged" or "runtime" or "internalcall" or "forwardref" or "preservesig")
            {
                throw new ReplException($"session methods have IL bodies; '{word}' is not supported");
            }
            else
            {
                throw new ReplException($"unexpected '{word}' after the parameter list");
            }
        }

        var parameters = ParseParameters(
            s.Substring(firstParen + 1, close - firstParen - 1).Trim(),
            memberContext,
            allowVarArg: convention.HasFlag(CallingConventions.VarArgs));
        var parameterSymbols = typeParameters;
        var constraints = typeParameterSpecs.Select((parameter, i) => new GenericParameterSymbol(
            parameterSymbols[i].Owner,
            true,
            i,
            parameter.Name,
            parameter.Attributes,
            [.. parameter.ConstraintTexts.Select(t => SymbolBinder.BindType(CilSyntaxParser.ParseType(t), memberContext).Type)])).ToList();

        if (name == ".ctor")
        {
            if (isStatic || !SymbolIdentity.Equal(returnType, TypeSymbol.Void))
            {
                throw new ReplException(".ctor must be 'instance void .ctor(...)'");
            }

            if (typeParameters.Length > 0)
            {
                throw new ReplException("a constructor cannot be generic");
            }

            if (owner.Kind == TypeKind.Interface)
            {
                throw new ReplException("an interface has no constructor");
            }

            attributes |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        }
        else if (name == ".cctor")
        {
            if (!isStatic || !SymbolIdentity.Equal(returnType, TypeSymbol.Void) || parameters.Count > 0)
            {
                throw new ReplException(".cctor must be 'static void .cctor()' with no parameters");
            }

            attributes |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        }

        if (owner.Kind == TypeKind.Enum)
        {
            throw new ReplException("an enum cannot declare methods");
        }

        var isVirtual = attributes.HasFlag(MethodAttributes.Virtual);
        var isAbstract = attributes.HasFlag(MethodAttributes.Abstract);
        if (isAbstract && !isVirtual)
        {
            throw new ReplException($"abstract methods must be virtual: add 'virtual' to {name}");
        }

        if (isAbstract && owner.Kind != TypeKind.Interface && !owner.Attributes.HasFlag(TypeAttributes.Abstract))
        {
            throw new ReplException($"{name} is abstract but {owner.Name} is not; add 'abstract' to the .class header");
        }

        if ((attributes.HasFlag(MethodAttributes.NewSlot) || attributes.HasFlag(MethodAttributes.Final)) && !isVirtual)
        {
            throw new ReplException($"'{(attributes.HasFlag(MethodAttributes.NewSlot) ? "newslot" : "final")}' needs 'virtual'");
        }

        if (isVirtual && isStatic && owner.Kind != TypeKind.Interface)
        {
            throw new ReplException("a static method cannot be virtual outside an interface");
        }

        if (isVirtual && (name is ".ctor" or ".cctor"))
        {
            throw new ReplException("a constructor cannot be virtual");
        }

        if (owner.Kind == TypeKind.Interface && isVirtual && !isStatic)
        {
            // Every virtual instance member of an interface introduces a slot.
            attributes |= MethodAttributes.NewSlot;
        }

        return new MethodSymbol
        {
            Definition = DefinitionId.None,
            Source = MethodSymbolSource.Declared,
            Name = name,
            ReturnType = returnType,
            Parameters = parameters,
            Attributes = attributes,
            ImplAttributes = impl,
            CallingConvention = convention,
            ReturnRequiredModifiers = returnRequired,
            ReturnOptionalModifiers = returnOptional,
            GenericParameters = constraints,
        };
    }

    /// <summary>
    /// Finds the parameter list after any return-type modifiers and generic parameter list.
    /// </summary>
    private static int FindParameterList(string s, int from)
    {
        var angle = 0;
        for (var i = from; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<')
            {
                angle++;
            }
            else if (c == '>')
            {
                angle--;
            }
            else if (c == '\'')
            {
                var close = s.IndexOf('\'', i + 1);
                if (close < 0)
                {
                    return -1;
                }

                i = close;
            }
            else if (c == '(')
            {
                if (angle == 0 && !IsModifierOrPointerParen(s, i))
                {
                    return i;
                }

                i = TypeParser.FindMatchingParen(s, i);
            }
        }

        return -1;
    }

    private static bool IsModifierOrPointerParen(string s, int paren)
    {
        var end = paren;
        while (end > 0 && char.IsWhiteSpace(s[end - 1]))
        {
            end--;
        }

        if (end > 0 && s[end - 1] == '*')
        {
            return true;
        }

        var start = end;
        while (start > 0 && char.IsLetter(s[start - 1]))
        {
            start--;
        }

        return s[start..end] is "modreq" or "modopt";
    }

    private static int FindNameStart(string s, int from, int paren)
    {
        // The name is the last whitespace-separated token before the parameter list, taking a
        // quoted name or a generic parameter list as one token.
        var depth = 0;
        var i = paren - 1;
        while (i >= from && char.IsWhiteSpace(s[i]))
        {
            i--;
        }

        while (i >= from)
        {
            var c = s[i];
            if (c == '>')
            {
                depth++;
            }
            else if (c == '<')
            {
                depth--;
            }
            else if (c == '\'' && depth == 0)
            {
                var open = s.LastIndexOf('\'', i - 1);
                if (open >= from)
                {
                    return open;
                }
            }
            else if (char.IsWhiteSpace(c) && depth == 0)
            {
                return i + 1;
            }

            i--;
        }

        return from;
    }

    private static List<ParameterSymbol> ParseParameters(string inner, IBindingScope context, bool allowVarArg)
    {
        var parameters = new List<ParameterSymbol>();
        if (inner.Length == 0)
        {
            return parameters;
        }

        foreach (var raw in TypeParser.SplitTopLevel(inner))
        {
            var (part, parameterAttributes) = ReadParameterAttributes(raw);
            if (part == "...")
            {
                if (!allowVarArg)
                {
                    throw new ReplException("a '...' parameter needs the vararg calling convention: .method public vararg ...");
                }

                if (parameters.Count > 0 && raw != TypeParser.SplitTopLevel(inner)[^1])
                {
                    throw new ReplException("'...' must be the last parameter");
                }

                continue;
            }

            var at = 0;
            var type = BindTypeAt(part, ref at, context, out _, out var required, out var optional);
            var parameterName = InstructionParser.Unquote(part[at..].Trim());
            if (parameterName.Length > 0 && !InstructionParser.IsIdentifier(parameterName))
            {
                throw new ReplException($"bad parameter name '{parameterName}'");
            }

            if (parameterName.Length > 0 && parameters.Any(p => p.Name == parameterName))
            {
                throw new ReplException($"parameter '{parameterName}' is already declared");
            }

            if (SymbolIdentity.Equal(type, TypeSymbol.Void))
            {
                throw new ReplException("a parameter cannot be void");
            }

            parameters.Add(new ParameterSymbol(type, parameterName.Length == 0 ? null : parameterName)
            {
                Attributes = parameterAttributes,
                RequiredModifiers = required,
                OptionalModifiers = optional,
            });
        }

        return parameters;
    }

    private static (string Part, ParameterAttributes Attributes) ReadParameterAttributes(string part)
    {
        var attributes = ParameterAttributes.None;
        while (part.StartsWith('['))
        {
            var bracket = part.IndexOf(']', StringComparison.Ordinal);
            if (bracket < 0)
            {
                throw new ReplException($"bad parameter declaration '{part}'");
            }

            var attribute = part[1..bracket].Trim();
            switch (attribute)
            {
                case "in":
                    attributes |= ParameterAttributes.In;
                    break;
                case "out":
                    attributes |= ParameterAttributes.Out;
                    break;
                case "opt":
                    attributes |= ParameterAttributes.Optional;
                    break;
                default:
                    return (part, attributes);
            }

            part = part[(bracket + 1)..].Trim();
        }

        return (part, attributes);
    }

    private static string PeekWord(string s, int pos)
    {
        var end = pos;
        while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_'))
        {
            end++;
        }

        return s[pos..end];
    }

    /// <summary>
    /// Parses the text after <c>.method</c>.
    /// </summary>
    /// <param name="spec">The header text.</param>
    /// <param name="context">The parse context; its methods are the session's table, its generics are empty.</param>
    /// <param name="opensBlock">True when the header ended with <c>{</c>.</param>
    /// <returns>The signature.</returns>
    /// <exception cref="ReplException">The header is malformed or uses unsupported features.</exception>
    public static MethodSymbol Parse(string spec, IBindingScope context, out bool opensBlock)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var s = TypeParser.Normalize(spec).Trim();
        opensBlock = false;
        if (s.EndsWith('{'))
        {
            opensBlock = true;
            s = s[..^1].TrimEnd();
        }

        var pos = 0;
        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            if (TypeParser.TryKeyword(s, ref pos, "instance"))
            {
                throw new ReplException("session methods are static; remove 'instance'");
            }

            if (TypeParser.TryKeyword(s, ref pos, "vararg"))
            {
                throw new ReplException("session methods cannot be vararg (only the cell can, with .vararg)");
            }

            var matched = false;
            foreach (var modifier in Modifiers)
            {
                if (TypeParser.TryKeyword(s, ref pos, modifier))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                break;
            }
        }

        // The return type comes first and may itself contain parentheses (modopt, a function
        // pointer), so it is parsed as a type rather than split at the first '('. A header with
        // nothing before the name has no return type.
        var firstParen = FindParameterList(s, pos);
        var beforeParen = (firstParen < 0 ? s[pos..] : s[pos..firstParen]).Trim();
        if (firstParen < 0 || beforeParen.Length == 0 || !beforeParen.Any(char.IsWhiteSpace))
        {
            throw new ReplException(Usage);
        }

        var returnType = BindTypeAt(s, ref pos, context, out _);
        TypeParser.SkipWhitespace(s, ref pos);
        var name = ReadName(s, ref pos);
        TypeParser.SkipWhitespace(s, ref pos);
        if (name.Length == 0 || pos >= s.Length || s[pos] != '(')
        {
            throw new ReplException(Usage);
        }

        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException($"bad method name '{name}'");
        }

        if (name is "Run" or "Invoke")
        {
            throw new ReplException($"'{name}' is reserved for the cell method; pick another name");
        }

        var close = TypeParser.FindMatchingParen(s, pos);
        foreach (var word in s[(close + 1)..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Trailers.Contains(word))
            {
                throw new ReplException($"unexpected '{word}' after the parameter list");
            }
        }

        var parameters = new List<ParameterSymbol>();
        var inner = s.Substring(pos + 1, close - pos - 1).Trim();
        if (inner.Length > 0)
        {
            foreach (var raw in TypeParser.SplitTopLevel(inner))
            {
                var part = StripParameterAttributes(raw);
                if (part == "...")
                {
                    throw new ReplException("session methods cannot be vararg (only the cell can, with .vararg)");
                }

                var at = 0;
                var type = BindTypeAt(part, ref at, context, out _);
                var parameterName = InstructionParser.Unquote(part[at..].Trim());
                if (parameterName.Length > 0 && !InstructionParser.IsIdentifier(parameterName))
                {
                    throw new ReplException($"bad parameter name '{parameterName}'");
                }

                if (parameterName.Length > 0 && parameters.Any(p => p.Name == parameterName))
                {
                    throw new ReplException($"parameter '{parameterName}' is already declared");
                }

                if (SymbolIdentity.Equal(type, TypeSymbol.Void))
                {
                    throw new ReplException("a parameter cannot be void");
                }

                parameters.Add(new ParameterSymbol(type, parameterName.Length == 0 ? null : parameterName));
            }
        }

        return new MethodSymbol
        {
            Definition = DefinitionId.None,
            Source = MethodSymbolSource.Session,
            Name = name,
            ReturnType = returnType,
            Parameters = parameters,
            Attributes = MethodAttributes.Public | MethodAttributes.Static,
        };
    }

    private static string ReadName(string s, ref int pos)
    {
        if (pos < s.Length && s[pos] == '\'')
        {
            var end = s.IndexOf('\'', pos + 1);
            if (end < 0)
            {
                throw new ReplException("unterminated quoted name");
            }

            var quoted = s[(pos + 1)..end];
            pos = end + 1;
            return quoted;
        }

        var start = pos;
        while (pos < s.Length && s[pos] != '(' && !char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }

        return s[start..pos];
    }

    private static string StripParameterAttributes(string part)
    {
        // [in], [out], and [opt] carry no meaning here. Anything else in brackets is an assembly
        // qualifier on the type and stays for the type parser.
        while (part.StartsWith('['))
        {
            var bracket = part.IndexOf(']', StringComparison.Ordinal);
            if (bracket < 0)
            {
                throw new ReplException($"bad parameter declaration '{part}'");
            }

            var attribute = part[1..bracket].Trim();
            if (attribute is not ("in" or "out" or "opt"))
            {
                break;
            }

            part = part[(bracket + 1)..].Trim();
        }

        return part;
    }

    private static TypeSymbol BindTypeAt(string text, ref int position, IBindingScope scope, out bool pinned) =>
        BindTypeAt(text, ref position, scope, out pinned, out _, out _);

    private static TypeSymbol BindTypeAt(
        string text,
        ref int position,
        IBindingScope scope,
        out bool pinned,
        out IReadOnlyList<TypeSymbol> required,
        out IReadOnlyList<TypeSymbol> optional)
    {
        var bound = SymbolBinder.BindType(CilSyntaxParser.ParseTypeAt(text, ref position), scope);
        pinned = bound.Pinned;
        required = bound.RequiredModifiers;
        optional = bound.OptionalModifiers;
        return bound.Type;
    }
}
