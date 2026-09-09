using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Binds syntax to symbols through a scope: the type a written name means, the member a reference
/// names among its overloads, with the substitutions, the tie-breaks, and the messages the
/// resolver has always used. The binder makes every decision; the scope only answers questions,
/// so the same decisions hold for an actual line and for a preview of one.
/// </summary>
public static class SymbolBinder
{
    /// <summary>
    /// Binds a type, keeping its top-level <c>pinned</c> and custom modifiers apart.
    /// </summary>
    /// <param name="syntax">The type syntax.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The bound type.</returns>
    /// <exception cref="ReplException">The type cannot be found or does not fit its arguments.</exception>
    public static BoundType BindType(TypeSyntax syntax, IBindingScope scope) => BindType(syntax, scope, lenientGenerics: false);

    /// <summary>
    /// Binds a type, with a <c>!N</c> beyond what is in scope standing for <c>object</c> when
    /// <paramref name="lenientGenerics"/> is set: the first pass over a member reference, before
    /// the declaring type's own parameters are known.
    /// </summary>
    /// <param name="syntax">The type syntax.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="lenientGenerics">True to tolerate out-of-scope generic parameter indices.</param>
    /// <returns>The bound type.</returns>
    /// <exception cref="ReplException">The type cannot be found or does not fit its arguments.</exception>
    public static BoundType BindType(TypeSyntax syntax, IBindingScope scope, bool lenientGenerics)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(scope);
        var type = BindCore(syntax.Unwrapped, scope, lenientGenerics);
        var required = syntax.Modifiers(true).Select(m => BindType(m, scope, lenientGenerics).Type).ToList();
        var optional = syntax.Modifiers(false).Select(m => BindType(m, scope, lenientGenerics).Type).ToList();
        return new BoundType(type, syntax.IsPinned, required, optional);
    }

    private static TypeSymbol BindCore(TypeSyntax syntax, IBindingScope scope, bool lenient)
    {
        switch (syntax.Kind)
        {
            case TypeSyntaxKind.Primitive:
                return syntax.Keyword == CilPrimitives.DecimalAlias ? scope.LookupDecimal() : TypeSymbol.Primitive(syntax.Keyword!);
            case TypeSyntaxKind.TypeParameter:
            case TypeSyntaxKind.MethodParameter:
            {
                var generics = lenient ? scope.Generics.Lenient() : scope.Generics;
                return generics.Resolve(syntax.Kind == TypeSyntaxKind.MethodParameter, syntax.Reference!);
            }

            case TypeSyntaxKind.Named:
            {
                var result = scope.LookupType(syntax.Name!, syntax.AssemblyHint, syntax.Arguments.Count, syntax.ValueTypeKeyword);
                if (syntax.Arguments.Count == 0)
                {
                    return result.Type;
                }

                var arguments = syntax.Arguments.Select(a => BindType(a, scope, lenient).Type).ToList();
                var definition = result.Type;
                if (!result.FromSession && !definition.IsGenericDefinition)
                {
                    throw new ReplException($"'{scope.Pretty(definition)}' is not a generic type definition");
                }

                var arity = scope.GenericArgumentsOf(definition).Count;
                if (arity != arguments.Count)
                {
                    throw new ReplException($"'{scope.Pretty(definition)}' takes {arity} type argument(s), not {arguments.Count}");
                }

                if (definition.Kind != TypeSymbolKind.Named)
                {
                    throw new ReplException($"'{scope.Pretty(definition)}' is not a generic type definition");
                }

                return TypeSymbol.Construct(definition, arguments);
            }

            case TypeSyntaxKind.Array:
            {
                var element = BindCore(syntax.Element!, scope, lenient);
                return syntax.IsVector ? TypeSymbol.SzArray(element) : TypeSymbol.Array(element, syntax.Rank, [], []);
            }

            case TypeSyntaxKind.ByRef:
                return TypeSymbol.ByRef(BindCore(syntax.Element!, scope, lenient));
            case TypeSyntaxKind.Pointer:
                return TypeSymbol.Pointer(BindCore(syntax.Element!, scope, lenient));
            case TypeSyntaxKind.FunctionPointer:
                return TypeSymbol.FunctionPointer(BindFunctionPointer(syntax.FunctionPointer!, scope, lenient));
            case TypeSyntaxKind.Modified:
            case TypeSyntaxKind.Pinned:
                // A modifier or pinning inside another type has no place in the type model; the
                // modifier itself is still bound, so a wrong name in it is reported.
                foreach (var modifier in syntax.Modifiers(true).Concat(syntax.Modifiers(false)))
                {
                    BindType(modifier, scope, lenient);
                }

                return BindCore(syntax.Unwrapped, scope, lenient);
            default:
                throw new ReplException("expected a type");
        }
    }

    private static MethodSignatureSymbol BindFunctionPointer(FunctionPointerSyntax syntax, IBindingScope scope, bool lenient)
    {
        var (managed, unmanaged, convention) = Conventions(syntax.ConventionWords);
        var returnType = BindType(syntax.ReturnType, scope, lenient).Type;
        var parameters = syntax.Parameters.Select(p => BindType(p, scope, lenient).Type).ToList();
        return new MethodSignatureSymbol(managed, unmanaged, convention, returnType, parameters, syntax.SentinelIndex);
    }

    /// <summary>
    /// The calling convention the words of a signature spell.
    /// </summary>
    /// <param name="words">The words, in order: <c>instance</c>, <c>explicit</c>, <c>vararg</c>, <c>unmanaged</c>, <c>cdecl</c>, and the rest.</param>
    /// <returns>The managed convention, whether the signature is unmanaged, and the unmanaged convention.</returns>
    public static (CallingConventions Managed, bool IsUnmanaged, System.Runtime.InteropServices.CallingConvention Unmanaged) Conventions(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var managed = CallingConventions.Standard;
        var isUnmanaged = false;
        var unmanaged = System.Runtime.InteropServices.CallingConvention.Winapi;
        foreach (var word in words)
        {
            switch (word)
            {
                case "instance":
                    managed |= CallingConventions.HasThis;
                    break;
                case "explicit":
                    managed |= CallingConventions.ExplicitThis;
                    break;
                case "vararg":
                    managed = (managed & ~CallingConventions.Standard) | CallingConventions.VarArgs;
                    break;
                case "unmanaged":
                    isUnmanaged = true;
                    break;
                case "cdecl":
                    isUnmanaged = true;
                    unmanaged = System.Runtime.InteropServices.CallingConvention.Cdecl;
                    break;
                case "stdcall":
                    isUnmanaged = true;
                    unmanaged = System.Runtime.InteropServices.CallingConvention.StdCall;
                    break;
                case "thiscall":
                    isUnmanaged = true;
                    unmanaged = System.Runtime.InteropServices.CallingConvention.ThisCall;
                    break;
                case "fastcall":
                    isUnmanaged = true;
                    unmanaged = System.Runtime.InteropServices.CallingConvention.FastCall;
                    break;
                default:
                    break;
            }
        }

        return (managed, isUnmanaged, unmanaged);
    }

    /// <summary>
    /// Binds a method reference to the member it names, or refuses with the resolver's message.
    /// </summary>
    /// <param name="syntax">The reference syntax.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="wantConstructor">True when the call site is <c>newobj</c> and a constructor is required.</param>
    /// <returns>The bound member.</returns>
    /// <exception cref="ReplException">No unique member matches.</exception>
    public static BoundMethod BindMethodReference(MemberSyntax syntax, IBindingScope scope, bool wantConstructor)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(scope);
        if (syntax.IsSessionForm)
        {
            return BindSessionMethod(syntax, scope, wantConstructor);
        }

        var name = syntax.Name;

        // The declaring type must be known before !N can be resolved, so the left side is bound
        // twice: once leniently to find the declaring type, then again with that type's arguments in scope.
        var declaring = BindType(syntax.DeclaringType!, scope, lenientGenerics: true).Type;
        var typeArguments = scope.GenericArgumentsOf(declaring);
        if (syntax.GenericArity is int arity)
        {
            return BindGenericDefinition(syntax, declaring, typeArguments, scope);
        }

        var methodArguments = syntax.GenericArguments is null
            ? scope.Generics.MethodArguments
            : [.. syntax.GenericArguments.Select(a => BindType(a, scope).Type)];
        var memberScope = scope.WithGenerics(new SymbolGenericContext(typeArguments, methodArguments));
        BindType(syntax.DeclaringType!, memberScope);
        var returnType = syntax.ReturnType is null ? null : BindType(syntax.ReturnType, memberScope).Type;

        IReadOnlyList<TypeSymbol>? parameterTypes = null;
        IReadOnlyList<TypeSymbol>? optionalTypes = null;
        if (syntax.Parameters is not null)
        {
            parameterTypes = [.. syntax.FixedParameters.Select(p => BindType(p, memberScope).Type)];
            optionalTypes = syntax.OptionalParameters is null ? null : [.. syntax.OptionalParameters.Select(p => BindType(p, memberScope).Type)];
        }

        var explicitMethodArguments = syntax.GenericArguments is null ? null : methodArguments;
        if (scope.TryGetDeclaration(declaring, out var own))
        {
            return BindOwnMethod(own, declaring, scope, name, parameterTypes, returnType, syntax.ExplicitInstance, wantConstructor || name is ".ctor" or ".cctor", explicitMethodArguments, optionalTypes);
        }

        if (wantConstructor || name is ".ctor" or ".cctor")
        {
            return new BoundMethod(BindConstructor(declaring, name, parameterTypes, scope), null, optionalTypes);
        }

        var method = BindLoadedMethod(declaring, name, parameterTypes, explicitMethodArguments, returnType, syntax.ExplicitInstance, syntax.IsVarArg, scope);
        return new BoundMethod(method, null, optionalTypes ?? (syntax.IsVarArg && method.IsVarArg ? [] : null));
    }

    /// <summary>
    /// Binds a field reference to the field it names.
    /// </summary>
    /// <param name="syntax">The reference syntax.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The field.</returns>
    /// <exception cref="ReplException">The field does not exist.</exception>
    public static FieldSymbol BindFieldReference(MemberSyntax syntax, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(scope);
        if (syntax.ReturnType is not null)
        {
            BindType(syntax.ReturnType, scope, lenientGenerics: true);
        }

        var declaring = BindType(syntax.DeclaringType!, scope, lenientGenerics: true).Type;
        var name = syntax.Name;
        if (scope.TryGetDeclaration(declaring, out var own))
        {
            var found = own.FindField(name);
            if (found is null && own.BaseType is { } baseType && BindInheritedField(baseType, scope, name) is { } inheritedField)
            {
                return inheritedField;
            }

            if (found is null)
            {
                var declared = string.Join(", ", own.Fields.Select(f => f.Name));
                throw new ReplException(declared.Length == 0
                    ? $"no field '{name}' on {scope.Pretty(declaring)} (declare it with .field first)"
                    : $"no field '{name}' on {scope.Pretty(declaring)}; fields: {declared}");
            }

            return SymbolRelations.Instantiate(found, declaring);
        }

        if (scope.RequiresDefinitionLookup(declaring))
        {
            return scope.Field(declaring, name) ?? throw new ReplException($"no field '{name}' on {scope.Pretty(declaring)}");
        }

        if (scope.Field(declaring, name) is { } field)
        {
            return field;
        }

        var names = string.Join(", ", scope.Fields(declaring).Select(f => f.Name).Take(12));
        throw new ReplException(names.Length == 0
            ? $"no field '{name}' on {scope.Pretty(declaring)}"
            : $"no field '{name}' on {scope.Pretty(declaring)}; fields: {names}");
    }

    private static FieldSymbol? BindInheritedField(TypeSymbol baseType, IBindingScope scope, string name)
    {
        for (var current = baseType; current is not null; current = scope.BaseOf(current))
        {
            if (scope.TryGetDeclaration(current, out var baseOwn))
            {
                var found = baseOwn.FindField(name);
                if (found is not null)
                {
                    return SymbolRelations.Instantiate(found, current);
                }

                continue;
            }

            if (current.DefinitionOrSelf.Definition.IsDeclaration)
            {
                continue;
            }

            if (scope.Field(current, name) is { } field)
            {
                return field;
            }
        }

        return null;
    }

    private static BoundMethod BindSessionMethod(MemberSyntax syntax, IBindingScope scope, bool wantConstructor)
    {
        var returnType = syntax.ReturnType is null ? null : BindType(syntax.ReturnType, scope).Type;
        var name = syntax.Name;
        if (wantConstructor)
        {
            throw new ReplException("newobj needs a constructor (Type::.ctor(...)); session methods are static and are called with call");
        }

        if (syntax.ExplicitInstance)
        {
            throw new ReplException("session methods are static; drop 'instance'");
        }

        if (syntax.IsVarArg)
        {
            throw new ReplException("session methods are not vararg");
        }

        var methods = scope.SessionMethods;
        var signature = methods.FirstOrDefault(m => m.Name == name);
        if (signature is null)
        {
            throw new ReplException(methods.Count == 0
                ? $"no method '{name}' in the session (define one with .method, or write Type::{name}(...) for a framework method)"
                : $"no method '{name}' in the session; defined: {string.Join(", ", methods.Select(m => SymbolRenderer.DescribeSignature(m, scope.Pretty)))}  (define one with .method)");
        }

        if (returnType is not null && !SymbolIdentity.Equal(returnType, signature.ReturnType))
        {
            throw new ReplException($"method {name} returns {scope.Pretty(signature.ReturnType)}, not {scope.Pretty(returnType)}");
        }

        if (syntax.Parameters is not null)
        {
            var types = syntax.Parameters.Select(p => BindType(p, scope).Type).ToList();
            var expected = signature.ParameterTypes;
            if (types.Count != expected.Count || !types.Zip(expected).All(pair => SymbolIdentity.Equal(pair.First, pair.Second)))
            {
                throw new ReplException($"no method {name}({string.Join(", ", types.Select(scope.Pretty))}) in the session; defined: {SymbolRenderer.DescribeSignature(signature, scope.Pretty)}");
            }
        }

        return new BoundMethod(signature, null, null);
    }

    private static BoundMethod BindOwnMethod(IDeclarationMembers own, TypeSymbol declaring, IBindingScope scope, string name, IReadOnlyList<TypeSymbol>? parameterTypes, TypeSymbol? returnType, bool explicitInstance, bool wantConstructor, IReadOnlyList<TypeSymbol>? methodArguments, IReadOnlyList<TypeSymbol>? optionalTypes)
    {
        var instantiated = declaring.Kind == TypeSymbolKind.Constructed;
        var arity = methodArguments?.Count ?? 0;
        var candidates = own.FindMethods(name)
            .Where(m => m.GenericParameters.Count == arity)
            .Select(m => (Declared: m, Effective: SymbolRelations.Instantiate(m, declaring, methodArguments ?? [])))
            .Where(m => parameterTypes is null || ParametersMatch(m.Effective, parameterTypes))
            .ToList();
        if (candidates.Count > 1 && returnType is not null)
        {
            candidates = candidates.Where(c => SymbolIdentity.Equal(c.Effective.ReturnType, returnType)).ToList();
        }

        if (candidates.Count > 1 && explicitInstance)
        {
            candidates = candidates.Where(c => !c.Effective.IsStatic).ToList();
        }

        if (candidates.Count == 1)
        {
            var (signature, effective) = candidates[0];
            if (returnType is not null && !SymbolIdentity.Equal(effective.ReturnType, returnType))
            {
                throw new ReplException($"{scope.Pretty(declaring)}::{name} returns {scope.Pretty(effective.ReturnType)}, not {scope.Pretty(returnType)}");
            }

            if (wantConstructor && signature.Name != ".ctor")
            {
                throw new ReplException($"newobj needs a constructor; {name} is a method");
            }

            return new BoundMethod(effective, signature, optionalTypes);
        }

        if (candidates.Count == 0 && !wantConstructor && own.BaseType is { } baseType && BindInherited(baseType, scope, name, parameterTypes, returnType, explicitInstance, methodArguments, optionalTypes) is { } inherited)
        {
            return inherited;
        }

        if (candidates.Count == 0 && arity == 0 && returnType is not null && parameterTypes is not null && own.CanDefineForward && !instantiated && !scope.Inspecting)
        {
            // A member referenced before its declaration: the signature is taken at its word and
            // checked when the type closes, which is what lets members call each other in any order.
            var forward = new MethodSymbol
            {
                Definition = DefinitionId.None,
                Source = MethodSymbolSource.Forward,
                DeclaringType = declaring,
                Name = name,
                Attributes = wantConstructor ? MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName : explicitInstance ? MethodAttributes.Public : MethodAttributes.Public | MethodAttributes.Static,
                CallingConvention = wantConstructor || explicitInstance ? CallingConventions.HasThis : CallingConventions.Standard,
                ReturnType = returnType,
                Parameters = [.. parameterTypes.Select(t => new ParameterSymbol(t, null))],
                IsDeclared = false,
            };
            return new BoundMethod(own.DefineForward(forward), null, optionalTypes);
        }

        var known = own.FindMethods(name).ToList();
        if (known.Count == 0 && wantConstructor && !own.CanDefineForward)
        {
            throw new ReplException(NoConstructor(declaring, scope));
        }

        if (known.Count == 0)
        {
            var names = string.Join(", ", own.Methods.Where(m => m.IsDeclared).Select(m => SymbolRenderer.DescribeSignature(m, scope.Pretty)).Take(12));
            throw new ReplException(names.Length == 0
                ? $"no method '{name}' on {scope.Pretty(declaring)} yet (declare it, or reference it with its full signature to declare it later)"
                : $"no method '{name}' on {scope.Pretty(declaring)}; methods: {names}");
        }

        if (candidates.Count == 0)
        {
            throw new ReplException($"no overload {scope.Pretty(declaring)}::{name}({Signature(parameterTypes, scope)}); candidates:\n{string.Join("\n", known.Select(k => "    " + SymbolRenderer.DescribeMember(k, scope.Pretty)))}");
        }

        throw new ReplException($"ambiguous: {scope.Pretty(declaring)}::{name}; give parameter types. candidates:\n{string.Join("\n", candidates.Select(c => "    " + SymbolRenderer.DescribeMember(c.Declared, scope.Pretty)))}");
    }

    private static string NoConstructor(TypeSymbol declaring, IBindingScope scope)
    {
        var valueType = declaring.IsValueTypeShape;
        return $"{(valueType ? "struct" : "class")} {scope.Pretty(declaring)} declares no constructor (add a .method public instance void .ctor(...) to it{(valueType ? ", or use initobj" : "")})";
    }

    /// <summary>
    /// Finds a member a type being written inherits: from a base still being written through its
    /// declarations, from a loaded base through its members.
    /// </summary>
    private static BoundMethod? BindInherited(TypeSymbol baseType, IBindingScope scope, string name, IReadOnlyList<TypeSymbol>? parameterTypes, TypeSymbol? returnType, bool explicitInstance, IReadOnlyList<TypeSymbol>? methodArguments, IReadOnlyList<TypeSymbol>? optionalTypes)
    {
        var arity = methodArguments?.Count ?? 0;
        for (var current = baseType; current is not null; current = scope.BaseOf(current))
        {
            if (scope.TryGetDeclaration(current, out var baseOwn))
            {
                // A member declared ahead of its line, as a rebuild does, counts: the base's close
                // refuses a forward reference that no line claims.
                var found = baseOwn.FindMethods(name)
                    .Where(m => m.Name is not (".ctor" or ".cctor") && m.GenericParameters.Count == arity)
                    .Select(m => (Definition: m, Effective: SymbolRelations.Instantiate(m, current, methodArguments ?? [])))
                    .Where(m => parameterTypes is null || ParametersMatch(m.Effective, parameterTypes))
                    .Where(m => returnType is null || SymbolIdentity.Equal(m.Effective.ReturnType, returnType))
                    .ToList();
                if (found.Count == 1)
                {
                    return new BoundMethod(found[0].Effective, found[0].Definition, optionalTypes);
                }

                continue;
            }

            if (current.DefinitionOrSelf.Definition.IsDeclaration)
            {
                continue;
            }

            try
            {
                var method = BindLoadedMethod(current, name, parameterTypes, methodArguments, returnType, explicitInstance, optionalTypes is not null, scope);
                return new BoundMethod(method, null, optionalTypes);
            }
            catch (ReplException)
            {
                // Not declared there; the next base may have it.
            }
        }

        return null;
    }

    private static BoundMethod BindGenericDefinition(MemberSyntax syntax, TypeSymbol declaring, IReadOnlyList<TypeSymbol> typeArguments, IBindingScope scope)
    {
        var name = syntax.Name;
        var arity = syntax.GenericArity!.Value;
        if (scope.TryGetDeclaration(declaring, out _) || declaring.DefinitionOrSelf.Definition.IsDeclaration)
        {
            throw new ReplException($"{scope.Pretty(declaring)}::{name}<[{arity}]> names a member of a class being written; give its type arguments instead");
        }

        var matches = new List<MethodSymbol>();
        foreach (var candidate in scope.Methods(declaring, name).Where(m => m.IsGenericDefinition && m.Arity == arity))
        {
            var candidateScope = scope.WithGenerics(new SymbolGenericContext(typeArguments, [.. candidate.GenericParameters.Select(p => p.AsType)]));
            BindType(syntax.DeclaringType!, candidateScope);
            var returnType = syntax.ReturnType is null ? null : BindType(syntax.ReturnType, candidateScope).Type;
            if (returnType is not null && !SymbolIdentity.Equal(candidate.ReturnType, returnType))
            {
                continue;
            }

            if (syntax.Parameters is not null)
            {
                var parameterTypes = syntax.Parameters.Select(p => BindType(p, candidateScope).Type).ToList();
                if (!ParametersMatch(candidate, parameterTypes))
                {
                    continue;
                }
            }

            if (syntax.ExplicitInstance && candidate.IsStatic)
            {
                continue;
            }

            matches.Add(candidate);
        }

        return matches.Count switch
        {
            1 => new BoundMethod(matches[0], null, null),
            0 => throw new ReplException($"no generic method '{name}' with {arity} type parameter(s) and those parameters on {scope.Pretty(declaring)}"),
            _ => throw new ReplException($"ambiguous: {scope.Pretty(declaring)}::{name}<[{arity}]>; give parameter types"),
        };
    }

    private static MethodSymbol BindConstructor(TypeSymbol declaring, string name, IReadOnlyList<TypeSymbol>? parameterTypes, IBindingScope scope)
    {
        var isStatic = name == ".cctor";
        if (scope.RequiresDefinitionLookup(declaring))
        {
            var candidates = scope.Constructors(declaring, isStatic)
                .Where(c => parameterTypes is null || ParametersMatch(c, parameterTypes))
                .ToList();
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            throw new ReplException(candidates.Count == 0
                ? $"no constructor {scope.Pretty(declaring)}({Signature(parameterTypes, scope)})"
                : $"ambiguous constructor for {scope.Pretty(declaring)}; give parameter types");
        }

        var constructors = scope.Constructors(declaring, isStatic);
        if (constructors.Count == 0 && name == ".ctor" && scope.IsSessionType(declaring))
        {
            throw new ReplException(NoConstructor(declaring, scope));
        }

        var matched = parameterTypes is null ? constructors : [.. constructors.Where(c => ParametersMatch(c, parameterTypes))];
        if (matched.Count == 1)
        {
            return matched[0];
        }

        if (matched.Count == 0)
        {
            throw new ReplException($"no constructor {scope.Pretty(declaring)}({Signature(parameterTypes, scope)}); candidates:\n{Candidates(constructors, scope)}");
        }

        throw new ReplException($"ambiguous constructor for {scope.Pretty(declaring)}; candidates:\n{Candidates(matched, scope)}");
    }

    private static MethodSymbol BindLoadedMethod(
        TypeSymbol declaring,
        string name,
        IReadOnlyList<TypeSymbol>? parameterTypes,
        IReadOnlyList<TypeSymbol>? methodGenericArguments,
        TypeSymbol? returnType,
        bool explicitInstance,
        bool isVarArg,
        IBindingScope scope)
    {
        if (scope.RequiresDefinitionLookup(declaring))
        {
            var candidates = scope.Methods(declaring, name)
                .Where(m => !m.IsGenericDefinition)
                .Where(m => parameterTypes is null || ParametersMatch(m, parameterTypes))
                .ToList();
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            throw new ReplException(candidates.Count == 0
                ? $"no method '{name}' with those parameters on {scope.Pretty(declaring)}"
                : $"ambiguous: {scope.Pretty(declaring)}::{name}; give parameter types");
        }

        var methods = scope.Methods(declaring, name);
        if (methods.Count == 0)
        {
            throw new ReplException($"no method '{name}' on {scope.Pretty(declaring)}");
        }

        var closed = new List<MethodSymbol>();
        foreach (var m in methods)
        {
            if (methodGenericArguments is not null)
            {
                if (!m.IsGenericDefinition || m.Arity != methodGenericArguments.Count)
                {
                    continue;
                }

                if (scope.Instantiate(m, methodGenericArguments) is { } instantiated)
                {
                    closed.Add(instantiated);
                }
            }
            else if (!m.IsGenericDefinition)
            {
                closed.Add(m);
            }
        }

        var candidateSet = parameterTypes is null
            ? closed
            : closed.Where(m => ParametersMatch(m, parameterTypes)).ToList();

        if (isVarArg)
        {
            var varargs = candidateSet.Where(m => m.IsVarArg).ToList();
            if (varargs.Count > 0)
            {
                candidateSet = varargs;
            }
        }

        if (candidateSet.Count > 1 && returnType is not null)
        {
            var byReturn = candidateSet.Where(m => SymbolIdentity.Equal(m.ReturnType, returnType)).ToList();
            if (byReturn.Count > 0)
            {
                candidateSet = byReturn;
            }
        }

        if (candidateSet.Count > 1 && explicitInstance)
        {
            var instance = candidateSet.Where(m => !m.IsStatic).ToList();
            if (instance.Count > 0)
            {
                candidateSet = instance;
            }
        }

        if (candidateSet.Count > 1)
        {
            var visible = candidateSet.Where(m => m.IsPublic).ToList();
            if (visible.Count > 0)
            {
                candidateSet = visible;
            }

            var own = candidateSet.Where(m => SymbolIdentity.Equal(m.DeclaringType, declaring)).ToList();
            if (own.Count > 0)
            {
                candidateSet = own;
            }
        }

        if (candidateSet.Count == 1)
        {
            return candidateSet[0];
        }

        if (candidateSet.Count == 0)
        {
            throw new ReplException($"no overload {scope.Pretty(declaring)}::{name}({Signature(parameterTypes, scope)}); candidates:\n{Candidates(methods, scope)}");
        }

        throw new ReplException($"ambiguous: {scope.Pretty(declaring)}::{name}; give parameter types. candidates:\n{Candidates(candidateSet, scope)}");
    }

    /// <summary>
    /// True when a member's parameters are exactly the wanted types, by identity.
    /// </summary>
    /// <param name="method">The member.</param>
    /// <param name="wanted">The wanted parameter types.</param>
    /// <returns>True when they match.</returns>
    public static bool ParametersMatch(MethodSymbol method, IReadOnlyList<TypeSymbol> wanted)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(wanted);
        if (method.Parameters.Count != wanted.Count)
        {
            return false;
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (!SymbolIdentity.Equal(method.Parameters[i].Type, wanted[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string Signature(IReadOnlyList<TypeSymbol>? parameterTypes, IBindingScope scope) =>
        parameterTypes is null ? "" : string.Join(", ", parameterTypes.Select(scope.Pretty));

    private static string Candidates(IEnumerable<MethodSymbol> methods, IBindingScope scope) =>
        string.Join("\n", methods.Take(12).Select(m => "    " + scope.Describe(m)));
}
