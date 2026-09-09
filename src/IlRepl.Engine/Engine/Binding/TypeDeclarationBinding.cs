using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Validates completed type declarations through metadata and symbols in both runtime input and editing replay.
/// </summary>
internal static class TypeDeclarationBinding
{
    /// <summary>
    /// Checks the base chain, layout, constraints and implemented slots without creating or preparing a type.
    /// </summary>
    /// <param name="declaration">The completed declaration.</param>
    /// <param name="scope">The scope used to resolve its hierarchy and members.</param>
    /// <returns>Static interface mappings that emission must add for matching implementations.</returns>
    public static IReadOnlyList<OverrideSymbol> Validate(TypeValidationState declaration, IBindingScope scope)
    {
        var bases = BaseChain(declaration.Type, scope, declaration.Description);
        CheckKindAndLayout(declaration, scope);
        CheckConstraints(declaration, scope);
        var interfaces = Interfaces(declaration.Type, scope);
        foreach (var target in declaration.Overrides)
        {
            var owner = target.DeclaringType!;
            if (!bases.Concat(interfaces).Any(type => SymbolIdentity.Equal(type.DefinitionOrSelf, owner.DefinitionOrSelf)))
            {
                var hint = owner.IsInterface ? "add implements " : "extend ";
                throw new ReplException($"{declaration.Description} cannot .override {scope.Pretty(owner)}::{target.Name}: "
                    + $"{scope.Pretty(owner)} is not one of its bases or interfaces ({hint}{scope.Pretty(owner)})");
            }
        }

        if (declaration.Kind is TypeKind.Interface or TypeKind.Enum)
        {
            return [];
        }

        var own = DeclaredMethods(declaration.Type, scope).ToArray();
        CheckAbstractMembers(declaration, bases, own, scope);
        return CheckInterfaces(declaration, bases, interfaces, own, scope);
    }

    private static List<TypeSymbol> BaseChain(TypeSymbol type, IBindingScope scope, string what)
    {
        var seen = new HashSet<TypeSymbol> { type.DefinitionOrSelf };
        var bases = new List<TypeSymbol>();
        for (var current = scope.BaseOf(type); current is not null; current = scope.BaseOf(current))
        {
            if (!seen.Add(current.DefinitionOrSelf))
            {
                throw new ReplException($"{what} extends itself through {scope.Pretty(current)}");
            }

            if (bases.Count == 0)
            {
                var definition = current.DefinitionOrSelf;
                if (definition.Attributes.HasFlag(TypeAttributes.Sealed)
                    || definition.Kind == TypeSymbolKind.Primitive && definition.Keyword != "object")
                {
                    var word = scope.EnumUnderlyingType(current) is not null ? "enum"
                        : current.IsValueTypeShape ? "struct" : "class";
                    throw new ReplException($"{what} cannot extend sealed {word} {scope.Pretty(current)}");
                }

                if (definition.IsInterface)
                {
                    throw new ReplException($"{what} cannot extend interface {scope.Pretty(current)}; use implements");
                }
            }

            bases.Add(current);
        }

        return bases;
    }

    private static void CheckKindAndLayout(TypeValidationState declaration, IBindingScope scope)
    {
        var what = declaration.Description;
        if (declaration.Kind == TypeKind.Interface)
        {
            if (declaration.Layout != TypeLayoutKind.Auto || declaration.PackingSize is not null || declaration.ClassSize is not null)
            {
                throw new ReplException($"{what} is an interface; it has no layout, .pack, or .size");
            }

            if (scope.BaseOf(declaration.Type) is not null)
            {
                throw new ReplException($"{what} is an interface; it cannot extend a type");
            }
        }

        if (declaration.Kind == TypeKind.Enum)
        {
            var fields = scope.TryGetDeclaration(declaration.Type, out var members)
                ? members.Fields : scope.Fields(declaration.Type);
            if (fields.Count(field => field.Name == "value__") != 1)
            {
                throw new ReplException($"{what} needs exactly one value__ field: "
                    + ".field public specialname rtspecialname int32 value__");
            }

            if (declaration.PackingSize is not null || declaration.ClassSize is not null || declaration.Layout != TypeLayoutKind.Auto)
            {
                throw new ReplException($"{what} is an enum; it has no layout, .pack, or .size");
            }
        }

        if (declaration.Layout == TypeLayoutKind.Explicit)
        {
            var missing = declaration.Offsets.FirstOrDefault(field => field.Value is null).Key;
            if (missing is not null)
            {
                throw new ReplException($"{what} has explicit layout, so every instance field needs an offset; "
                    + $"{missing} has none (write .field [N] ...)");
            }
        }
    }

    private static void CheckConstraints(TypeValidationState declaration, IBindingScope scope)
    {
        foreach (var parameter in declaration.Parameters)
        {
            if (parameter.Attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)
                && parameter.Attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                throw new ReplException($"{declaration.Description}: generic parameter {parameter.Name} "
                    + "cannot be both class and valuetype");
            }

            var classes = parameter.Constraints.Where(type => !type.IsInterface && !type.IsGenericParameter).ToArray();
            if (classes.Length > 1)
            {
                throw new ReplException($"{declaration.Description}: generic parameter {parameter.Name} has more than one class constraint "
                    + $"({string.Join(", ", classes.Select(scope.Pretty))}); at most one is allowed");
            }
        }
    }

    private static void CheckAbstractMembers(
        TypeValidationState declaration, List<TypeSymbol> bases, MethodSymbol[] own, IBindingScope scope)
    {
        if (declaration.Type.Attributes.HasFlag(TypeAttributes.Abstract))
        {
            return;
        }

        var implemented = own.Where(method => !method.IsAbstract && method.IsVirtual
            && !method.Attributes.HasFlag(MethodAttributes.NewSlot)).ToList();
        foreach (var current in bases)
        {
            foreach (var method in DeclaredMethods(current, scope))
            {
                if (method.IsAbstract && !method.IsStatic)
                {
                    if (!implemented.Any(candidate => Matches(candidate, method))
                        && !declaration.Overrides.Any(target => SameTarget(target, method)))
                    {
                        throw new ReplException($"{declaration.Description} must implement abstract {Describe(method, scope)} "
                            + $"(declare a virtual method with that name and signature, or mark the {declaration.KindWord} abstract)");
                    }
                }
                else if (method.IsVirtual)
                {
                    implemented.Add(method);
                }
            }
        }
    }

    private static List<OverrideSymbol> CheckInterfaces(
        TypeValidationState declaration, List<TypeSymbol> bases, List<TypeSymbol> interfaces,
        MethodSymbol[] own, IBindingScope scope)
    {
        var implied = new List<OverrideSymbol>();
        var baseInterfaces = bases.Count == 0 ? [] : Interfaces(bases[0], scope);
        var inherited = bases.SelectMany(type => DeclaredMethods(type, scope))
            .Where(method => method.IsVirtual && !method.IsAbstract && !method.IsStatic).ToArray();
        foreach (var iface in interfaces)
        {
            if (baseInterfaces.Contains(iface))
            {
                continue;
            }

            foreach (var method in DeclaredMethods(iface, scope).Where(method => method.IsAbstract))
            {
                if (declaration.Overrides.Any(target => SameTarget(target, method)))
                {
                    continue;
                }

                var implementation = own.FirstOrDefault(candidate => Matches(candidate, method)
                    && (method.IsStatic || candidate.IsVirtual));
                if (implementation is not null)
                {
                    if (method.IsStatic)
                    {
                        implied.Add(new OverrideSymbol(new BoundMethod(method, null, null), implementation));
                    }

                    continue;
                }

                if (!method.IsStatic && inherited.Any(candidate => Matches(candidate, method)))
                {
                    continue;
                }

                var hint = method.IsStatic ? "declare a static method with that name and signature"
                    : "declare a public virtual method with that name and signature, or write .override "
                        + $"{scope.Pretty(iface)}::{method.Name} inside the method that implements it";
                if (own.Any(candidate => !method.IsStatic && Matches(candidate, method) && !candidate.IsVirtual))
                {
                    hint = $"{method.Name} matches but is not virtual; add virtual to its header";
                }

                throw new ReplException($"{declaration.Description} must implement {Describe(method, scope)} ({hint})");
            }
        }

        return implied;
    }

    private static IEnumerable<MethodSymbol> DeclaredMethods(TypeSymbol type, IBindingScope scope)
    {
        if (type.IsGenericParameter)
        {
            return [];
        }

        var methods = scope.TryGetDeclaration(type, out var declaration) ? declaration.Methods : scope.AllMethods(type);
        return methods.Where(method => method.IsDeclared && !method.IsConstructor
            && SymbolIdentity.Equal(method.DeclaringType!.DefinitionOrSelf, type.DefinitionOrSelf))
            .Select(method => SymbolRelations.Instantiate(method, type, []));
    }

    private static List<TypeSymbol> Interfaces(TypeSymbol type, IBindingScope scope)
    {
        var seen = new HashSet<TypeSymbol>();
        var result = new List<TypeSymbol>();
        var pending = new Stack<TypeSymbol>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var iface in scope.DeclaredInterfacesOf(current))
            {
                if (!result.Contains(iface))
                {
                    result.Add(iface);
                    pending.Push(iface);
                }
            }

            if (scope.BaseOf(current) is { } parent)
            {
                pending.Push(parent);
            }
        }

        return result;
    }

    private static bool Matches(MethodSymbol first, MethodSymbol second) => first.Name == second.Name
        && SignatureSymbolIdentity.Equal(first, second);

    private static string Describe(MethodSymbol method, IBindingScope scope) =>
        (method.IsStatic ? "static " : "") + scope.Describe(method);

    private static bool SameTarget(MethodSymbol first, MethodSymbol second) => first.Definition == second.Definition
        && SymbolIdentity.Equal(first.DeclaringType!, second.DeclaringType!);
}
