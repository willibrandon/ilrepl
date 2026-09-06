using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Resolves ILAsm method and field references against loaded types. The return type and the
/// <c>[assembly]</c> prefix are optional, short type names resolve through the common
/// <c>System.*</c> namespaces, and <c>!N</c>/<c>!!N</c> inside the reference follow ILAsm rules.
/// </summary>
public static class MemberResolver
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

        // Name, method generic arguments, and parameters come from the right-hand side.
        string name;
        string? parameterText = null;
        string? genericText = null;
        var paren = rest.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            var close = TypeParser.FindMatchingParen(rest, paren);
            name = rest[..paren].Trim();
            parameterText = rest.Substring(paren + 1, close - paren - 1);
            if (rest[(close + 1)..].Trim().Length > 0)
            {
                throw new ReplException($"unexpected '{rest[(close + 1)..].Trim()}' after parameter list");
            }
        }
        else
        {
            name = rest;
        }

        var lt = name.IndexOf('<', StringComparison.Ordinal);
        if (lt >= 0)
        {
            if (!name.EndsWith('>'))
            {
                throw new ReplException("unbalanced '<' in method name");
            }

            genericText = name[(lt + 1)..^1];
            name = name[..lt].Trim();
        }

        if (name.Length == 0)
        {
            throw new ReplException("missing method name");
        }

        // The declaring type must be known before !N can be resolved, so the left side is parsed
        // twice: once to find the declaring type, then again with that type's arguments in scope.
        var (_, declaring) = SplitLeft(left, context, lenient: true);
        var typeArguments = declaring.IsGenericType ? declaring.GetGenericArguments() : [];
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
        var name = s[(separator + 2)..].Trim();
        if (name.Length == 0)
        {
            throw new ReplException("missing field name");
        }

        if (declaring is TypeBuilder || (declaring.IsGenericType && declaring.GetGenericArguments().Any(a => a is GenericTypeParameterBuilder)))
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

    /// <summary>
    /// Renders a method in the same shape the resolver accepts, for candidate lists and
    /// diagnostics.
    /// </summary>
    /// <param name="method">The method or constructor.</param>
    /// <returns>The IL-style signature.</returns>
    public static string Describe(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var parameters = string.Join(", ", method.GetParameters().Select(p => TypeNameFormatter.Pretty(p.ParameterType)));
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

    private static int FindMemberSeparator(string s)
    {
        var depth = 0;
        for (var i = 0; i + 1 < s.Length; i++)
        {
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
        // While the declaring type is still unknown, !N cannot be resolved for real; any
        // placeholder works because only the declaring type is kept from this pass.
        var placeholders = Enumerable.Repeat(typeof(object), 32).ToArray();
        return new GenericContext(placeholders, generics.MethodArguments.Count > 0 ? generics.MethodArguments : placeholders);
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

    private static Type Substitute(Type type, Type[] definitionArguments, Type[] actualArguments)
    {
        if (type.IsGenericParameter && type.DeclaringMethod is null)
        {
            for (var i = 0; i < definitionArguments.Length; i++)
            {
                if (definitionArguments[i] == type)
                {
                    return actualArguments[i];
                }
            }
        }

        if (type.IsByRef)
        {
            return Substitute(type.GetElementType()!, definitionArguments, actualArguments).MakeByRefType();
        }

        if (type.IsArray)
        {
            var element = Substitute(type.GetElementType()!, definitionArguments, actualArguments);
            return type.GetArrayRank() == 1 && type == type.GetElementType()!.MakeArrayType() ? element.MakeArrayType() : element.MakeArrayType(type.GetArrayRank());
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var args = type.GetGenericArguments().Select(a => Substitute(a, definitionArguments, actualArguments)).ToArray();
            return type.GetGenericTypeDefinition().MakeGenericType(args);
        }

        return type;
    }

    internal static bool TypesEqual(Type a, Type b)
    {
        if (a == b)
        {
            return true;
        }

        // Generic parameter builders compare by position when both sides are builders from
        // different prototype methods; names are unique within a cell.
        if (a.IsGenericParameter && b.IsGenericParameter)
        {
            return a.Name == b.Name && a.GenericParameterPosition == b.GenericParameterPosition;
        }

        if (a.IsByRef && b.IsByRef)
        {
            return TypesEqual(a.GetElementType()!, b.GetElementType()!);
        }

        if (a.IsPointer && b.IsPointer)
        {
            return TypesEqual(a.GetElementType()!, b.GetElementType()!);
        }

        if (a.IsArray && b.IsArray)
        {
            // int32[] is a vector and int32[0...] is a rank-1 array; they are different types.
            return a.IsSZArray == b.IsSZArray && a.GetArrayRank() == b.GetArrayRank() && TypesEqual(a.GetElementType()!, b.GetElementType()!);
        }

        if (a.IsGenericType && b.IsGenericType && !a.IsGenericTypeDefinition && !b.IsGenericTypeDefinition)
        {
            if (a.GetGenericTypeDefinition() != b.GetGenericTypeDefinition())
            {
                return false;
            }

            var aa = a.GetGenericArguments();
            var ba = b.GetGenericArguments();
            if (aa.Length != ba.Length)
            {
                return false;
            }

            for (var i = 0; i < aa.Length; i++)
            {
                if (!TypesEqual(aa[i], ba[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static string Signature(Type[]? parameterTypes) =>
        parameterTypes is null ? "" : string.Join(", ", parameterTypes.Select(TypeNameFormatter.Pretty));

    private static string Candidates(IEnumerable<MethodBase> methods) =>
        string.Join("\n", methods.Take(12).Select(m => "    " + Describe(m)));
}
