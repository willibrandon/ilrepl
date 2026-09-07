using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Resolves ILAsm method and field references against loaded types. The return type and the
/// <c>[assembly]</c> prefix are optional, short type names resolve through the common
/// <c>System.*</c> namespaces, and <c>!N</c>/<c>!!N</c> inside the reference follow ILAsm rules.
/// </summary>
public static partial class MemberResolver
{
    private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

    /// <summary>
    /// Resolves a method reference such as <c>void [System.Console]System.Console::WriteLine(string)</c>,
    /// <c>instance string Object::ToString()</c>, <c>Console::WriteLine(string)</c>, <c>instance void StringBuilder::.ctor()</c>,
    /// <c>!!0 Enumerable::First&lt;int32&gt;(class IEnumerable`1&lt;!!0&gt;)</c>, or <c>vararg int32 Hello::CountArgs(..., int32, int32)</c>.
    /// </summary>
    /// <param name="spec">The reference text.</param>
    /// <param name="context">The parse context.</param>
    /// <param name="wantConstructor">True when the call site is <c>newobj</c> and a constructor is required.</param>
    /// <returns>The resolved method.</returns>
    /// <exception cref="ReplException">The reference is malformed, or no unique member matches.</exception>
    public static ResolvedMethod ResolveMethod(string spec, ParseContext context, bool wantConstructor)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var s = TypeParser.Normalize(spec).Trim();
        var explicitInstance = false;
        var isVarArg = false;
        var pos = 0;
        while (true)
        {
            TypeParser.SkipWhitespace(s, ref pos);
            if (TypeParser.TryKeyword(s, ref pos, "instance"))
            {
                explicitInstance = true;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "vararg"))
            {
                isVarArg = true;
            }
            else if (TypeParser.TryKeyword(s, ref pos, "default") || TypeParser.TryKeyword(s, ref pos, "explicit"))
            {
                // Accepted for completeness.
            }
            else
            {
                break;
            }
        }

        s = s[pos..];
        var separator = FindMemberSeparator(s);
        if (separator < 0)
        {
            return ResolveSessionMethod(s, context, wantConstructor, explicitInstance, isVarArg);
        }

        var left = s[..separator];
        var rest = s[(separator + 2)..].Trim();

        // Name, method generic arguments, and parameters come from the right-hand side. A quoted
        // name, '<Main>b__0_0', is read as one name however it is spelled inside the quotes.
        var (name, afterName) = ReadMemberName(rest);
        TypeParser.SkipWhitespace(rest, ref afterName);
        string? parameterText = null;
        string? genericText = null;
        var afterGeneric = afterName;
        if (afterName < rest.Length && rest[afterName] == '<')
        {
            var closeAngle = FindMatchingAngle(rest, afterName);
            genericText = rest[(afterName + 1)..closeAngle];
            afterGeneric = closeAngle + 1;
            TypeParser.SkipWhitespace(rest, ref afterGeneric);
        }

        var paren = afterGeneric < rest.Length && rest[afterGeneric] == '(' ? afterGeneric : rest.IndexOf('(', afterGeneric);
        if (paren >= 0)
        {
            if (rest[afterGeneric..paren].Trim().Length > 0)
            {
                throw new ReplException($"unexpected '{rest[afterGeneric..paren].Trim()}' before parameter list");
            }

            var close = TypeParser.FindMatchingParen(rest, paren);
            parameterText = rest.Substring(paren + 1, close - paren - 1);
            if (rest[(close + 1)..].Trim().Length > 0)
            {
                throw new ReplException($"unexpected '{rest[(close + 1)..].Trim()}' after parameter list");
            }
        }
        else if (rest[afterGeneric..].Trim().Length > 0)
        {
            throw new ReplException($"unexpected '{rest[afterGeneric..].Trim()}' in method reference");
        }

        if (name.Length == 0)
        {
            throw new ReplException("missing method name");
        }

        // <[N]> names a generic method definition of arity N without instantiating it, as ILAsm
        // spells a token for one.
        int? genericArity = null;
        if (genericText is not null && ArityMarker().Match(genericText) is { Success: true } arityMatch)
        {
            var arityValue = int.Parse(arityMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (arityValue < 1)
            {
                throw new ReplException($"expected a generic arity such as <[1]>, got '<{genericText}>'");
            }

            genericArity = arityValue;
            genericText = null;
        }

        // The declaring type must be known before !N can be resolved, so the left side is parsed
        // twice: once to find the declaring type, then again with that type's arguments in scope.
        var (_, declaring) = SplitLeft(left, context, lenient: true);
        var typeArguments = declaring.IsGenericType ? declaring.GetGenericArguments() : [];
        if (genericArity is int arity)
        {
            return ResolveGenericDefinition(declaring, name, arity, parameterText, left, context, typeArguments, explicitInstance);
        }

        var methodArguments = genericText is null
            ? context.Generics.MethodArguments
            : TypeParser.SplitTopLevel(genericText).Select(t => TypeParser.Parse(t, context)).ToArray();
        var memberContext = context.WithGenerics(new GenericContext(typeArguments, methodArguments));
        var (returnType, _) = SplitLeft(left, memberContext, lenient: false);

        Type[]? parameterTypes = null;
        Type[]? optionalTypes = null;
        if (parameterText is not null)
        {
            var fixedTypes = new List<Type>();
            List<Type>? optional = null;
            foreach (var part in TypeParser.SplitTopLevel(parameterText))
            {
                if (part == "...")
                {
                    if (optional is not null)
                    {
                        throw new ReplException("only one '...' is allowed in a parameter list");
                    }

                    optional = [];
                    isVarArg = true;
                    continue;
                }

                var t = TypeParser.Parse(part, memberContext);
                if (optional is null)
                {
                    fixedTypes.Add(t);
                }
                else
                {
                    optional.Add(t);
                }
            }

            parameterTypes = [.. fixedTypes];
            optionalTypes = optional?.ToArray();
        }

        if (context.Types.TryGetMembers(declaring, out var own))
        {
            return ResolveOwnMethod(own, declaring, context, name, parameterTypes, returnType, explicitInstance, wantConstructor || name is ".ctor" or ".cctor", genericText is null ? null : methodArguments, optionalTypes);
        }

        if (wantConstructor || name is ".ctor" or ".cctor")
        {
            return new ResolvedMethod(ResolveConstructor(declaring, name, parameterTypes), optionalTypes);
        }

        var method = ResolveMethodCore(declaring, name, parameterTypes, genericText is null ? null : methodArguments, returnType, explicitInstance, isVarArg);
        return new ResolvedMethod(method, optionalTypes ?? (isVarArg && method.CallingConvention.HasFlag(CallingConventions.VarArgs) ? Type.EmptyTypes : null));
    }

    /// <summary>
    /// Resolves a field reference such as <c>string [System.Runtime]System.String::Empty</c> or <c>int32 Counter::Count</c>.
    /// </summary>
    /// <param name="spec">The reference text.</param>
    /// <param name="context">The parse context.</param>
    /// <returns>The field.</returns>
    /// <exception cref="ReplException">The reference is malformed or the field does not exist.</exception>
    public static FieldInfo ResolveField(string spec, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(context);
        var s = TypeParser.Normalize(spec).Trim();
        var separator = FindMemberSeparator(s);
        if (separator < 0)
        {
            throw new ReplException("expected 'Type::field' in field reference");
        }

        var (_, declaring) = SplitLeft(s[..separator], context, lenient: true);
        var name = InstructionParser.Unquote(s[(separator + 2)..].Trim());
        if (name.Length == 0)
        {
            throw new ReplException("missing field name");
        }

        if (context.Types.TryGetMembers(declaring, out var own))
        {
            var found = own.FindField(name);
            if (found is null && own.BaseType is { } baseType && ResolveInheritedField(baseType, context, name) is { } inheritedField)
            {
                return inheritedField;
            }

            if (found is null)
            {
                var declared = string.Join(", ", own.Fields.Select(f => f.Declaration.Name));
                throw new ReplException(declared.Length == 0
                    ? $"no field '{name}' on {TypeNameFormatter.Pretty(declaring)} (declare it with .field first)"
                    : $"no field '{name}' on {TypeNameFormatter.Pretty(declaring)}; fields: {declared}");
            }

            return declaring.IsGenericType && !declaring.IsGenericTypeDefinition && found.Value.Builder is FieldBuilder fieldBuilder
                ? TypeBuilder.GetField(declaring, fieldBuilder)
                : found.Value.Builder;
        }

        if (declaring.IsGenericType && !declaring.IsGenericTypeDefinition && declaring.GetGenericArguments().Any(a => a is GenericTypeParameterBuilder))
        {
            var definitionField = declaring.GetGenericTypeDefinition().GetField(name, AllMembers)
                ?? throw new ReplException($"no field '{name}' on {TypeNameFormatter.Pretty(declaring)}");
            return TypeBuilder.GetField(declaring, definitionField);
        }

        var field = declaring.GetField(name, AllMembers);
        if (field is not null)
        {
            return field;
        }

        var names = string.Join(", ", declaring.GetFields(AllMembers).Select(f => f.Name).Take(12));
        throw new ReplException(names.Length == 0
            ? $"no field '{name}' on {TypeNameFormatter.Pretty(declaring)}"
            : $"no field '{name}' on {TypeNameFormatter.Pretty(declaring)}; fields: {names}");
    }

    private static FieldInfo? ResolveInheritedField(Type baseType, ParseContext context, string name)
    {
        for (Type? current = baseType; current is not null; current = TypeRelations.BaseTypeOf(current, context.Types))
        {
            if (context.Types.TryGetMembers(current, out var baseOwn))
            {
                var found = baseOwn.FindField(name);
                if (found is not null)
                {
                    return current.IsGenericType && !current.IsGenericTypeDefinition && found.Value.Builder is FieldBuilder fieldBuilder
                        ? TypeBuilder.GetField(current, fieldBuilder)
                        : found.Value.Builder;
                }

                continue;
            }

            if (current is TypeBuilder)
            {
                continue;
            }

            if (current.GetField(name, AllMembers) is { } field)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>
    /// Renders a method in the same shape the resolver accepts, for candidate lists and
    /// diagnostics.
    /// </summary>
    /// <param name="method">The method or constructor.</param>
    /// <returns>The IL-style signature.</returns>
    public static string Describe(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        string parameters;
        try
        {
            parameters = string.Join(", ", method.GetParameters().Select(p => TypeNameFormatter.Pretty(p.ParameterType)));
        }
        catch (NotSupportedException)
        {
            // A builder cannot list its parameters before its type is created.
            return $"{TypeNameFormatter.Pretty(method.DeclaringType)}::{method.Name}";
        }

        if (method.CallingConvention.HasFlag(CallingConventions.VarArgs))
        {
            parameters = parameters.Length == 0 ? "..." : parameters + ", ...";
        }

        var returnType = method is MethodInfo mi ? TypeNameFormatter.Pretty(mi.ReturnType) : "void";
        var instance = method.IsStatic ? "" : "instance ";
        var name = method is ConstructorInfo ? ".ctor" : method.Name;
        if (method is MethodInfo g && g.IsGenericMethod)
        {
            name += "<" + string.Join(", ", g.GetGenericArguments().Select(TypeNameFormatter.Pretty)) + ">";
        }

        return $"{instance}{returnType} {TypeNameFormatter.Pretty(method.DeclaringType)}::{name}({parameters})";
    }

    private static ResolvedMethod ResolveSessionMethod(string s, ParseContext context, bool wantConstructor, bool explicitInstance, bool isVarArg)
    {
        // "[ret] Name(params)" with no "::" names a method defined with .method. The return type
        // is optional, as it is for a framework method, and may contain parentheses of its own
        // (modopt, a function pointer), so it is parsed as a type when the text before the first
        // '(' has room for one. The parameter list, when given, must match.
        var firstParen = s.IndexOf('(', StringComparison.Ordinal);
        var head = (firstParen < 0 ? s : s[..firstParen]).Trim();
        Type? returnType = null;
        var pos = 0;
        if (head.Any(char.IsWhiteSpace))
        {
            returnType = TypeParser.ParseAt(s, ref pos, context, out _);
            TypeParser.SkipWhitespace(s, ref pos);
        }

        var nameEnd = pos;
        while (nameEnd < s.Length && s[nameEnd] != '(' && !char.IsWhiteSpace(s[nameEnd]))
        {
            nameEnd++;
        }

        var name = InstructionParser.Unquote(s[pos..nameEnd]);
        var afterName = nameEnd;
        TypeParser.SkipWhitespace(s, ref afterName);
        var paren = afterName < s.Length && s[afterName] == '(' ? afterName : -1;
        if (paren < 0 && afterName < s.Length)
        {
            throw new ReplException($"unexpected '{s[afterName..]}' in method reference");
        }

        if (name.Contains('<', StringComparison.Ordinal))
        {
            throw new ReplException("session methods are not generic");
        }

        if (!InstructionParser.IsIdentifier(name))
        {
            throw new ReplException("expected 'Type::Method(...)' in method reference (or a session method name defined with .method)");
        }

        if (wantConstructor)
        {
            throw new ReplException("newobj needs a constructor (Type::.ctor(...)); session methods are static and are called with call");
        }

        if (explicitInstance)
        {
            throw new ReplException("session methods are static; drop 'instance'");
        }

        if (isVarArg)
        {
            throw new ReplException("session methods are not vararg");
        }

        var signature = context.Methods.FirstOrDefault(m => m.Name == name);
        if (signature is null)
        {
            throw new ReplException(context.Methods.Count == 0
                ? $"no method '{name}' in the session (define one with .method, or write Type::{name}(...) for a framework method)"
                : $"no method '{name}' in the session; defined: {string.Join(", ", context.Methods.Select(m => m.Describe()))}  (define one with .method)");
        }

        if (returnType is not null && !TypesEqual(returnType, signature.ReturnType))
        {
            throw new ReplException($"method {name} returns {TypeNameFormatter.Pretty(signature.ReturnType)}, not {TypeNameFormatter.Pretty(returnType)}");
        }

        if (paren >= 0)
        {
            var close = TypeParser.FindMatchingParen(s, paren);
            var trailing = s[(close + 1)..].Trim();
            if (trailing.Length > 0)
            {
                throw new ReplException($"unexpected '{trailing}' after parameter list");
            }

            var parts = TypeParser.SplitTopLevel(s.Substring(paren + 1, close - paren - 1));
            if (parts.Contains("..."))
            {
                throw new ReplException("session methods are not vararg");
            }

            var types = parts.Select(p => TypeParser.Parse(p, context)).ToArray();
            var expected = signature.ParameterTypes;
            if (types.Length != expected.Length || !types.Zip(expected).All(pair => TypesEqual(pair.First, pair.Second)))
            {
                throw new ReplException($"no method {name}({string.Join(", ", types.Select(TypeNameFormatter.Pretty))}) in the session; defined: {signature.Describe()}");
            }
        }

        return new ResolvedMethod(signature);
    }

    private static ResolvedMethod ResolveOwnMethod(OwnMembers own, Type declaring, ParseContext context, string name, Type[]? parameterTypes, Type? returnType, bool explicitInstance, bool wantConstructor, IReadOnlyList<Type>? methodArguments, Type[]? optionalTypes)
    {
        var instantiated = declaring.IsGenericType && !declaring.IsGenericTypeDefinition;
        var definitionArguments = instantiated ? declaring.GetGenericTypeDefinition().GetGenericArguments() : null;
        var actualArguments = instantiated ? declaring.GetGenericArguments() : null;
        var arity = methodArguments?.Count ?? 0;
        MethodSignature Substituted(MethodSignature signature)
        {
            var effective = instantiated ? SubstituteSignature(signature, definitionArguments!, actualArguments!) : signature;
            if (methodArguments is { Count: > 0 })
            {
                // The call names the method's own arguments; its parameters are found in the signature.
                var parameters = SignatureIdentity.MethodParametersOf(signature);
                effective = effective with
                {
                    ReturnType = TypeRelations.SubstituteParameters(effective.ReturnType, parameters, methodArguments),
                    Parameters = [.. effective.Parameters.Select(p => p with { Type = TypeRelations.SubstituteParameters(p.Type, parameters, methodArguments) })],
                };
            }

            return effective;
        }

        var candidates = own.FindMethods(name)
            .Where(m => m.Signature.TypeParameters.Count == arity)
            .Select(m => (m.Signature, m.Builder, Declared: m.Declared, Effective: Substituted(m.Signature)))
            .Where(m => parameterTypes is null || (m.Effective.Parameters.Count == parameterTypes.Length && m.Effective.ParameterTypes.Zip(parameterTypes).All(p => TypeIdentity.Equal(p.First, p.Second))))
            .ToList();
        if (candidates.Count > 1 && returnType is not null)
        {
            candidates = candidates.Where(c => TypeIdentity.Equal(c.Effective.ReturnType, returnType)).ToList();
        }

        if (candidates.Count > 1 && explicitInstance)
        {
            candidates = candidates.Where(c => !c.Effective.IsStatic).ToList();
        }

        if (candidates.Count == 1)
        {
            var (signature, builder, _, effective) = candidates[0];
            if (returnType is not null && !TypeIdentity.Equal(effective.ReturnType, returnType))
            {
                throw new ReplException($"{TypeNameFormatter.Pretty(declaring)}::{name} returns {TypeNameFormatter.Pretty(effective.ReturnType)}, not {TypeNameFormatter.Pretty(returnType)}");
            }

            if (wantConstructor && signature.Name != ".ctor")
            {
                throw new ReplException($"newobj needs a constructor; {name} is a method");
            }

            // The builder itself is kept; the declaring type carries the instantiation, and the
            // writer builds the member reference on it.
            return new ResolvedMethod(builder, effective, declaring) { OptionalParameterTypesOverride = optionalTypes, DeclaredDefinition = signature, GenericArguments = methodArguments is { Count: > 0 } ? [.. methodArguments] : null };
        }

        if (candidates.Count == 0 && !wantConstructor && own.BaseType is { } baseType && ResolveInherited(baseType, context, name, parameterTypes, returnType, explicitInstance, methodArguments, optionalTypes) is { } inherited)
        {
            return inherited;
        }

        if (candidates.Count == 0 && arity == 0 && returnType is not null && parameterTypes is not null && own.DefineForward is not null && !instantiated && !context.Inspecting)
        {
            // A member referenced before its declaration: the signature is taken at its word and
            // checked when the type closes, which is what lets members call each other in any order.
            var signature = new MethodSignature(name, returnType, [.. parameterTypes.Select(t => new ArgumentDeclaration(t, null, null, ""))])
            {
                Attributes = wantConstructor ? MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName : explicitInstance ? MethodAttributes.Public : MethodAttributes.Public | MethodAttributes.Static,
                CallingConvention = wantConstructor || explicitInstance ? CallingConventions.HasThis : CallingConventions.Standard,
            };
            var forward = own.DefineForward(signature);
            return new ResolvedMethod(forward, signature, declaring) { OptionalParameterTypesOverride = optionalTypes };
        }

        var known = own.FindMethods(name).ToList();
        if (known.Count == 0 && wantConstructor && own.DefineForward is null)
        {
            throw new ReplException($"{(declaring.IsValueType ? "struct" : "class")} {TypeNameFormatter.Pretty(declaring)} declares no constructor (add a .method public instance void .ctor(...) to it{(declaring.IsValueType ? ", or use initobj" : "")})");
        }

        if (known.Count == 0)
        {
            var names = string.Join(", ", own.Methods.Where(m => m.Declared).Select(m => m.Signature.Describe()).Take(12));
            throw new ReplException(names.Length == 0
                ? $"no method '{name}' on {TypeNameFormatter.Pretty(declaring)} yet (declare it, or reference it with its full signature to declare it later)"
                : $"no method '{name}' on {TypeNameFormatter.Pretty(declaring)}; methods: {names}");
        }

        if (candidates.Count == 0)
        {
            throw new ReplException($"no overload {TypeNameFormatter.Pretty(declaring)}::{name}({Signature(parameterTypes)}); candidates:\n{string.Join("\n", known.Select(k => "    " + k.Signature.DescribeMember()))}");
        }

        throw new ReplException($"ambiguous: {TypeNameFormatter.Pretty(declaring)}::{name}; give parameter types. candidates:\n{string.Join("\n", candidates.Select(c => "    " + c.Signature.DescribeMember()))}");
    }

    /// <summary>
    /// Finds a member a type being written inherits: from a base still being written through
    /// its declarations, from a loaded base through reflection.
    /// </summary>
    private static ResolvedMethod? ResolveInherited(Type baseType, ParseContext context, string name, Type[]? parameterTypes, Type? returnType, bool explicitInstance, IReadOnlyList<Type>? methodArguments, Type[]? optionalTypes)
    {
        var arity = methodArguments?.Count ?? 0;
        for (Type? current = baseType; current is not null; current = TypeRelations.BaseTypeOf(current, context.Types))
        {
            if (context.Types.TryGetMembers(current, out var baseOwn))
            {
                // A member declared ahead of its line, as a rebuild does, counts: the base's close
                // refuses a forward reference that no line claims.
                var instantiated = current.IsGenericType && !current.IsGenericTypeDefinition;
                var definitionArguments = instantiated ? current.GetGenericTypeDefinition().GetGenericArguments() : null;
                var actualArguments = instantiated ? current.GetGenericArguments() : null;
                var found = baseOwn.FindMethods(name)
                    .Where(m => m.Signature.Name != ".ctor" && m.Signature.Name != ".cctor" && m.Signature.TypeParameters.Count == arity)
                    .Select(m => (m.Builder, Definition: m.Signature, Effective: Instantiate(instantiated ? SubstituteSignature(m.Signature, definitionArguments!, actualArguments!) : m.Signature, m.Signature, methodArguments)))
                    .Where(m => parameterTypes is null || (m.Effective.Parameters.Count == parameterTypes.Length && m.Effective.ParameterTypes.Zip(parameterTypes).All(p => TypeIdentity.Equal(p.First, p.Second))))
                    .Where(m => returnType is null || TypeIdentity.Equal(m.Effective.ReturnType, returnType))
                    .ToList();
                if (found.Count == 1)
                {
                    return new ResolvedMethod(found[0].Builder, found[0].Effective, current) { OptionalParameterTypesOverride = optionalTypes, DeclaredDefinition = found[0].Definition, GenericArguments = methodArguments is { Count: > 0 } ? [.. methodArguments] : null };
                }

                continue;
            }

            if (current is System.Reflection.Emit.TypeBuilder)
            {
                continue;
            }

            try
            {
                var method = ResolveMethodCore(current, name, parameterTypes, methodArguments, returnType, explicitInstance, optionalTypes is not null);
                return new ResolvedMethod(method, optionalTypes);
            }
            catch (ReplException)
            {
                // Not declared there; the next base may have it.
            }
        }

        return null;
    }

    /// <summary>
    /// Substitutes a method's own parameters, found in its definition, with the call's arguments.
    /// </summary>
    private static MethodSignature Instantiate(MethodSignature effective, MethodSignature definition, IReadOnlyList<Type>? methodArguments)
    {
        if (methodArguments is not { Count: > 0 })
        {
            return effective;
        }

        var parameters = SignatureIdentity.MethodParametersOf(definition);
        return effective with
        {
            ReturnType = TypeRelations.SubstituteParameters(effective.ReturnType, parameters, methodArguments),
            Parameters = [.. effective.Parameters.Select(p => p with { Type = TypeRelations.SubstituteParameters(p.Type, parameters, methodArguments) })],
        };
    }

    private static MethodSignature SubstituteSignature(MethodSignature signature, Type[] definitionArguments, Type[] actualArguments)
    {
        var parameters = signature.Parameters.Select(p => p with { Type = Substitute(p.Type, definitionArguments, actualArguments) }).ToList();
        return signature with { ReturnType = Substitute(signature.ReturnType, definitionArguments, actualArguments), Parameters = parameters };
    }

    private static int FindMemberSeparator(string s)
    {
        var depth = 0;
        var quoted = false;
        for (var i = 0; i + 1 < s.Length; i++)
        {
            if (s[i] == '\'')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                continue;
            }

            switch (s[i])
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    depth--;
                    break;
                case ':' when depth == 0 && s[i + 1] == ':':
                    return i;
                default:
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads the member name at the start of the text after <c>::</c>: a quoted name up to its
    /// closing quote, otherwise everything before a generic argument list or a parameter list.
    /// </summary>
    private static (string Name, int End) ReadMemberName(string rest)
    {
        if (rest.Length > 0 && rest[0] == '\'')
        {
            var close = rest.IndexOf('\'', 1);
            if (close < 0)
            {
                throw new ReplException("unterminated quote in member name");
            }

            return (rest[1..close], close + 1);
        }

        var end = 0;
        while (end < rest.Length && rest[end] != '<' && rest[end] != '(' && !char.IsWhiteSpace(rest[end]))
        {
            end++;
        }

        return (rest[..end], end);
    }

    private static int FindMatchingAngle(string s, int open)
    {
        var depth = 0;
        var quoted = false;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (quoted)
            {
                continue;
            }
            else if (s[i] == '<')
            {
                depth++;
            }
            else if (s[i] == '>' && --depth == 0)
            {
                return i;
            }
        }

        throw new ReplException("unbalanced '<' in method name");
    }

    /// <summary>
    /// The arity form of a generic argument list: a bracketed integer alone, <c>[1]</c>, which is
    /// not a type, unlike <c>[System.Runtime]System.String[]</c>.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\[\s*([0-9]+)\s*\]\s*$")]
    private static partial System.Text.RegularExpressions.Regex ArityMarker();

    /// <summary>
    /// Resolves a generic method definition by arity, <c>Name&lt;[N]&gt;(...)</c>, keeping it open. The
    /// parameter list is read against each candidate's own parameters, so <c>!!0</c> means that
    /// candidate's first parameter.
    /// </summary>
    private static ResolvedMethod ResolveGenericDefinition(Type declaring, string name, int arity, string? parameterText, string left, ParseContext context, Type[] typeArguments, bool explicitInstance)
    {
        if (context.Types.TryGetMembers(declaring, out _) || declaring is TypeBuilder)
        {
            throw new ReplException($"{TypeNameFormatter.Pretty(declaring)}::{name}<[{arity}]> names a member of a class being written; give its type arguments instead");
        }

        var matches = new List<MethodInfo>();
        foreach (var candidate in declaring.GetMethods(AllMembers).Where(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == arity))
        {
            var candidateContext = context.WithGenerics(new GenericContext(typeArguments, candidate.GetGenericArguments()));
            var (returnType, _) = SplitLeft(left, candidateContext, lenient: false);
            if (returnType is not null && candidate.ReturnType != returnType)
            {
                continue;
            }

            if (parameterText is not null)
            {
                var parameterTypes = TypeParser.SplitTopLevel(parameterText).Where(p => p != "...").Select(p => TypeParser.Parse(p, candidateContext)).ToArray();
                if (!ParametersMatch(candidate.GetParameters(), parameterTypes, null, null))
                {
                    continue;
                }
            }

            if (explicitInstance && candidate.IsStatic)
            {
                continue;
            }

            matches.Add(candidate);
        }

        return matches.Count switch
        {
            1 => new ResolvedMethod(matches[0], null),
            0 => throw new ReplException($"no generic method '{name}' with {arity} type parameter(s) and those parameters on {TypeNameFormatter.Pretty(declaring)}"),
            _ => throw new ReplException($"ambiguous: {TypeNameFormatter.Pretty(declaring)}::{name}<[{arity}]>; give parameter types"),
        };
    }

    private static (Type? ReturnType, Type Declaring) SplitLeft(string left, ParseContext context, bool lenient)
    {
        left = left.Trim();
        var ctx = lenient ? context.WithGenerics(LenientGenerics(context.Generics)) : context;
        var pos = 0;
        var first = TypeParser.ParseAt(left, ref pos, ctx, out _);
        TypeParser.SkipWhitespace(left, ref pos);
        if (pos >= left.Length)
        {
            return (null, first);
        }

        var second = TypeParser.ParseAt(left, ref pos, ctx, out _);
        TypeParser.SkipWhitespace(left, ref pos);
        if (pos < left.Length)
        {
            throw new ReplException($"unexpected '{left[pos..]}' in member reference");
        }

        return (first, second);
    }

    private static GenericContext LenientGenerics(GenericContext generics)
    {
        // While the declaring type is still unknown, a !N that names the member's own type
        // parameters cannot be resolved for real; any placeholder works because only the
        // declaring type is kept from this pass. Parameters that are in scope, a generic type's
        // own !N inside its body or a method's !!N, are real and stay so: they can appear in the
        // declaring type itself, as in Box`1<!0>::Count.
        // A !N beyond what is in scope belongs to the referenced type and gets a placeholder.
        var placeholders = Enumerable.Repeat(typeof(object), 32);
        return new GenericContext([.. generics.TypeArguments, .. placeholders], [.. generics.MethodArguments, .. placeholders]);
    }

    private static ConstructorInfo ResolveConstructor(Type declaring, string name, Type[]? parameterTypes)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (name == ".cctor" ? BindingFlags.Static : BindingFlags.Instance);
        if (declaring.IsGenericType && declaring.GetGenericArguments().Any(a => a is GenericTypeParameterBuilder))
        {
            var definition = declaring.GetGenericTypeDefinition();
            var map = definition.GetGenericArguments();
            var candidates = definition.GetConstructors(flags)
                .Where(c => parameterTypes is null || ParametersMatch(c.GetParameters(), parameterTypes, map, declaring.GetGenericArguments()))
                .ToArray();
            if (candidates.Length == 1)
            {
                return TypeBuilder.GetConstructor(declaring, candidates[0]);
            }

            throw new ReplException(candidates.Length == 0
                ? $"no constructor {TypeNameFormatter.Pretty(declaring)}({Signature(parameterTypes)})"
                : $"ambiguous constructor for {TypeNameFormatter.Pretty(declaring)}; give parameter types");
        }

        var constructors = declaring.GetConstructors(flags);
        if (constructors.Length == 0 && name == ".ctor" && TypeRelations.IsSessionType(declaring))
        {
            throw new ReplException($"{(declaring.IsValueType ? "struct" : "class")} {TypeNameFormatter.Pretty(declaring)} declares no constructor (add a .method public instance void .ctor(...) to it{(declaring.IsValueType ? ", or use initobj" : "")})");
        }

        var matched = parameterTypes is null ? constructors : constructors.Where(c => ParametersMatch(c.GetParameters(), parameterTypes, null, null)).ToArray();
        if (matched.Length == 1)
        {
            return matched[0];
        }

        if (matched.Length == 0)
        {
            throw new ReplException($"no constructor {TypeNameFormatter.Pretty(declaring)}({Signature(parameterTypes)}); candidates:\n{Candidates(constructors)}");
        }

        throw new ReplException($"ambiguous constructor for {TypeNameFormatter.Pretty(declaring)}; candidates:\n{Candidates(matched)}");
    }

    private static MethodInfo ResolveMethodCore(
        Type declaring,
        string name,
        Type[]? parameterTypes,
        IReadOnlyList<Type>? methodGenericArguments,
        Type? returnType,
        bool explicitInstance,
        bool isVarArg)
    {
        // Types instantiated over a cell's generic parameters cannot be reflected over directly;
        // resolve against the generic definition and map through TypeBuilder.GetMethod.
        if (declaring.IsGenericType && declaring.GetGenericArguments().Any(a => a is GenericTypeParameterBuilder))
        {
            var definition = declaring.GetGenericTypeDefinition();
            var map = definition.GetGenericArguments();
            var actual = declaring.GetGenericArguments();
            var candidates = definition.GetMethods(AllMembers)
                .Where(m => m.Name == name && !m.IsGenericMethodDefinition)
                .Where(m => parameterTypes is null || ParametersMatch(m.GetParameters(), parameterTypes, map, actual))
                .ToArray();
            if (candidates.Length == 1)
            {
                return TypeBuilder.GetMethod(declaring, candidates[0]);
            }

            throw new ReplException(candidates.Length == 0
                ? $"no method '{name}' with those parameters on {TypeNameFormatter.Pretty(declaring)}"
                : $"ambiguous: {TypeNameFormatter.Pretty(declaring)}::{name}; give parameter types");
        }

        var methods = declaring.GetMethods(AllMembers).Where(m => m.Name == name).ToArray();
        if (methods.Length == 0)
        {
            throw new ReplException($"no method '{name}' on {TypeNameFormatter.Pretty(declaring)}");
        }

        var closed = new List<MethodInfo>();
        foreach (var m in methods)
        {
            if (methodGenericArguments is not null)
            {
                if (!m.IsGenericMethodDefinition || m.GetGenericArguments().Length != methodGenericArguments.Count)
                {
                    continue;
                }

                try
                {
                    closed.Add(m.MakeGenericMethod([.. methodGenericArguments]));
                }
                catch (ArgumentException)
                {
                    // Constraint violation; not a candidate.
                }
            }
            else if (!m.IsGenericMethodDefinition)
            {
                closed.Add(m);
            }
        }

        var candidateSet = parameterTypes is null
            ? closed.ToArray()
            : closed.Where(m => ParametersMatch(m.GetParameters(), parameterTypes, null, null)).ToArray();

        if (isVarArg)
        {
            var varargs = candidateSet.Where(m => m.CallingConvention.HasFlag(CallingConventions.VarArgs)).ToArray();
            if (varargs.Length > 0)
            {
                candidateSet = varargs;
            }
        }

        if (candidateSet.Length > 1 && returnType is not null)
        {
            var byReturn = candidateSet.Where(m => m.ReturnType == returnType).ToArray();
            if (byReturn.Length > 0)
            {
                candidateSet = byReturn;
            }
        }

        if (candidateSet.Length > 1 && explicitInstance)
        {
            var instance = candidateSet.Where(m => !m.IsStatic).ToArray();
            if (instance.Length > 0)
            {
                candidateSet = instance;
            }
        }

        if (candidateSet.Length > 1)
        {
            var visible = candidateSet.Where(m => m.IsPublic).ToArray();
            if (visible.Length > 0)
            {
                candidateSet = visible;
            }

            var own = candidateSet.Where(m => m.DeclaringType == declaring).ToArray();
            if (own.Length > 0)
            {
                candidateSet = own;
            }
        }

        if (candidateSet.Length == 1)
        {
            return candidateSet[0];
        }

        if (candidateSet.Length == 0)
        {
            throw new ReplException($"no overload {TypeNameFormatter.Pretty(declaring)}::{name}({Signature(parameterTypes)}); candidates:\n{Candidates(methods)}");
        }

        throw new ReplException($"ambiguous: {TypeNameFormatter.Pretty(declaring)}::{name}; give parameter types. candidates:\n{Candidates(candidateSet)}");
    }

    private static bool ParametersMatch(ParameterInfo[] parameters, Type[] wanted, Type[]? definitionArguments, Type[]? actualArguments)
    {
        if (parameters.Length != wanted.Length)
        {
            return false;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var declared = parameters[i].ParameterType;
            if (definitionArguments is not null && actualArguments is not null)
            {
                declared = Substitute(declared, definitionArguments, actualArguments);
            }

            if (!TypesEqual(declared, wanted[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static Type Substitute(Type type, Type[] definitionArguments, Type[] actualArguments) => TypeRelations.Substitute(type, definitionArguments, actualArguments);

    internal static bool TypesEqual(Type a, Type b) => TypeIdentity.Equal(a, b);

    private static string Signature(Type[]? parameterTypes) =>
        parameterTypes is null ? "" : string.Join(", ", parameterTypes.Select(TypeNameFormatter.Pretty));

    private static string Candidates(IEnumerable<MethodBase> methods) =>
        string.Join("\n", methods.Take(12).Select(m => "    " + Describe(m)));
}
