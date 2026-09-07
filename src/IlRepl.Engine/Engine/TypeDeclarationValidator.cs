using System.Reflection;

namespace IlRepl.Engine;

/// <summary>
/// Checks a declared type the way the loader would before the family is written: every
/// abstract member it inherits is implemented, every interface it claims is implemented,
/// its overrides name slots it can fill, and its kind, layout, and constraints are consistent.
/// Refusing here keeps the message in the REPL's words instead of a <see cref="TypeLoadException"/>.
/// </summary>
public static class TypeDeclarationValidator
{
    /// <summary>
    /// One method as the validator sees it, whatever type it came from.
    /// </summary>
    private sealed record Shape(string Name, bool IsStatic, Type ReturnType, IReadOnlyList<Type> Parameters, bool IsAbstract, bool IsVirtual, bool IsNewSlot, Type Owner)
    {
        public bool Matches(Shape other) =>
            Name == other.Name && IsStatic == other.IsStatic && Parameters.Count == other.Parameters.Count
            && TypeIdentity.Equal(ReturnType, other.ReturnType)
            && Parameters.Zip(other.Parameters).All(p => TypeIdentity.Equal(p.First, p.Second));

        public string Describe() =>
            $"{(IsStatic ? "static " : "instance ")}{TypeNameFormatter.Pretty(ReturnType)} {TypeNameFormatter.Pretty(Owner)}::{Name}({string.Join(", ", Parameters.Select(TypeNameFormatter.Pretty))})";
    }

    /// <summary>
    /// Validates one declaration of a family.
    /// </summary>
    /// <param name="declaration">The declaration.</param>
    /// <param name="type">Its prototype or runtime type.</param>
    /// <param name="types">The table that knows the types being written.</param>
    /// <param name="declarationOf">Finds the declaration of a session type, or null for a framework type.</param>
    /// <returns>The overrides the writer must add for static abstract interface members matched by name and signature.</returns>
    /// <exception cref="ReplException">The declaration is not loadable, with the reason.</exception>
    public static IReadOnlyList<ClassOverrideDeclaration> Validate(TypeDeclaration declaration, Type type, TypeTable types, Func<Type, TypeDeclaration?> declarationOf)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(declarationOf);
        var what = declaration.KindWord + " " + declaration.FullName;
        CheckBaseChain(type, types, declarationOf, what);
        CheckKindAndLayout(declaration, what);
        CheckConstraints(declaration, what);
        CheckOverrideTargets(declaration, type, types, what);
        if (declaration.Kind is TypeKind.Interface or TypeKind.Enum)
        {
            return [];
        }

        CheckAbstractMembers(declaration, type, types, declarationOf, what);
        return CheckInterfaces(declaration, type, types, declarationOf, what);
    }

    private static void CheckBaseChain(Type type, TypeTable types, Func<Type, TypeDeclaration?> declarationOf, string what)
    {
        var seen = new HashSet<Type>(ReferenceEqualityComparer.Instance) { TypeRelations.Definition(type) };
        for (var current = TypeRelations.BaseTypeOf(type, types); current is not null; current = TypeRelations.BaseTypeOf(current, types))
        {
            var definition = TypeRelations.Definition(current);
            if (!seen.Add(definition))
            {
                throw new ReplException($"{what} extends itself through {TypeNameFormatter.Pretty(current)}");
            }

            if (ReferenceEquals(current, TypeRelations.BaseTypeOf(type, types)))
            {
                var baseDeclaration = declarationOf(definition);
                var isSealed = baseDeclaration?.Attributes.HasFlag(TypeAttributes.Sealed) ?? (definition is not System.Reflection.Emit.TypeBuilder && definition.IsSealed);
                var isInterface = baseDeclaration?.Kind == TypeKind.Interface || (definition is not System.Reflection.Emit.TypeBuilder && definition.IsInterface);
                var kindWord = baseDeclaration?.KindWord ?? (definition.IsEnum ? "enum" : definition.IsValueType ? "struct" : "class");
                var baseName = baseDeclaration?.FullName ?? TypeNameFormatter.Pretty(definition);
                if (isSealed)
                {
                    throw new ReplException($"{what} cannot extend sealed {kindWord} {baseName}");
                }

                if (isInterface)
                {
                    throw new ReplException($"{what} cannot extend interface {baseName}; use implements");
                }
            }
        }
    }

    private static void CheckKindAndLayout(TypeDeclaration declaration, string what)
    {
        if (declaration.Kind == TypeKind.Interface)
        {
            if (declaration.Layout != TypeLayoutKind.Auto || declaration.PackingSize is not null || declaration.ClassSize is not null)
            {
                throw new ReplException($"{what} is an interface; it has no layout, .pack, or .size");
            }

            if (declaration.BaseType is not null)
            {
                throw new ReplException($"{what} is an interface; it cannot extend a type");
            }
        }

        if (declaration.Kind == TypeKind.Enum)
        {
            var value = declaration.Fields.Count(f => f.Name == "value__");
            if (value != 1)
            {
                throw new ReplException($"{what} needs exactly one value__ field: .field public specialname rtspecialname int32 value__");
            }

            if (declaration.PackingSize is not null || declaration.ClassSize is not null || declaration.Layout != TypeLayoutKind.Auto)
            {
                throw new ReplException($"{what} is an enum; it has no layout, .pack, or .size");
            }
        }

        if (declaration.Layout == TypeLayoutKind.Explicit)
        {
            var missing = declaration.Fields.FirstOrDefault(f => !f.IsStatic && f.Offset is null);
            if (missing is not null)
            {
                throw new ReplException($"{what} has explicit layout, so every instance field needs an offset; {missing.Name} has none (write .field [N] ...)");
            }
        }
    }

    private static void CheckConstraints(TypeDeclaration declaration, string what)
    {
        foreach (var parameter in declaration.TypeParameters)
        {
            if (parameter.Attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) && parameter.Attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                throw new ReplException($"{what}: generic parameter {parameter.Name} cannot be both class and valuetype");
            }

            var classes = parameter.Constraints.Where(c => !c.IsInterface && !c.IsGenericParameter).ToList();
            if (classes.Count > 1)
            {
                throw new ReplException($"{what}: generic parameter {parameter.Name} has more than one class constraint ({string.Join(", ", classes.Select(TypeNameFormatter.Pretty))}); at most one is allowed");
            }
        }
    }

    private static void CheckOverrideTargets(TypeDeclaration declaration, Type type, TypeTable types, string what)
    {
        var interfaces = TypeRelations.AllInterfacesOf(type, types);
        var targets = declaration.Methods.SelectMany(m => m.Overrides.Select(o => o.Target)).Concat(declaration.Overrides.Select(o => o.Target));
        foreach (var target in targets)
        {
            var owner = target.DeclaringType!;
            var inHierarchy = (TypeRelations.IsSameOrSubclassDefinition(type, owner, types) && !ReferenceEquals(TypeRelations.Definition(owner), TypeRelations.Definition(type)))
                || interfaces.Any(i => ReferenceEquals(TypeRelations.Definition(i), TypeRelations.Definition(owner)));
            if (!inHierarchy)
            {
                var hint = owner.IsInterface ? $"add implements {TypeNameFormatter.Pretty(owner)}" : $"extend {TypeNameFormatter.Pretty(owner)}";
                throw new ReplException($"{what} cannot .override {TypeNameFormatter.Pretty(owner)}::{target.Name}: {TypeNameFormatter.Pretty(owner)} is not one of its bases or interfaces ({hint})");
            }
        }
    }

    private static void CheckAbstractMembers(TypeDeclaration declaration, Type type, TypeTable types, Func<Type, TypeDeclaration?> declarationOf, string what)
    {
        if (declaration.Attributes.HasFlag(TypeAttributes.Abstract))
        {
            return;
        }

        var own = ShapesOf(type, types, declarationOf).ToList();
        var explicitTargets = ExplicitTargets(declaration);
        var implemented = own.Where(s => !s.IsAbstract && s.IsVirtual && !s.IsNewSlot).ToList();
        for (var current = TypeRelations.BaseTypeOf(type, types); current is not null; current = TypeRelations.BaseTypeOf(current, types))
        {
            foreach (var shape in ShapesOf(current, types, declarationOf))
            {
                if (shape.IsAbstract && !shape.IsStatic)
                {
                    var done = implemented.Any(s => s.Matches(shape)) || explicitTargets.Any(t => TargetIs(t, shape, current, types));
                    if (!done)
                    {
                        throw new ReplException($"{what} must implement abstract {shape.Describe()} (declare a virtual method with that name and signature, or mark the {declaration.KindWord} abstract)");
                    }
                }
                else if (shape.IsVirtual)
                {
                    implemented.Add(shape);
                }
            }
        }
    }

    private static List<ClassOverrideDeclaration> CheckInterfaces(TypeDeclaration declaration, Type type, TypeTable types, Func<Type, TypeDeclaration?> declarationOf, string what)
    {
        var implied = new List<ClassOverrideDeclaration>();
        var baseType = TypeRelations.BaseTypeOf(type, types);
        var baseInterfaces = baseType is null ? [] : TypeRelations.AllInterfacesOf(baseType, types);
        var own = ShapesOf(type, types, declarationOf).ToList();
        var explicitTargets = ExplicitTargets(declaration);
        var inherited = new List<Shape>();
        for (var current = baseType; current is not null; current = TypeRelations.BaseTypeOf(current, types))
        {
            inherited.AddRange(ShapesOf(current, types, declarationOf).Where(s => s.IsVirtual && !s.IsAbstract && !s.IsStatic));
        }

        foreach (var iface in TypeRelations.AllInterfacesOf(type, types))
        {
            if (baseInterfaces.Any(b => TypeIdentity.Equal(b, iface)))
            {
                // The base type loaded with this interface, so its map already covers every member.
                continue;
            }

            foreach (var shape in ShapesOf(iface, types, declarationOf))
            {
                if (!shape.IsAbstract)
                {
                    continue;
                }

                if (explicitTargets.Any(t => TargetIs(t, shape, iface, types)))
                {
                    continue;
                }

                var implicitMatch = own.FirstOrDefault(s => s.Matches(shape) && (shape.IsStatic || s.IsVirtual));
                if (implicitMatch is not null)
                {
                    if (shape.IsStatic)
                    {
                        var target = TargetOf(iface, shape, types, declarationOf);
                        implied.Add(new ClassOverrideDeclaration(target, shape.Describe(), implicitMatch.Name, implicitMatch.ReturnType, implicitMatch.Parameters, true, ""));
                    }

                    continue;
                }

                if (!shape.IsStatic && inherited.Any(s => s.Matches(shape)))
                {
                    continue;
                }

                var hint = shape.IsStatic
                    ? "declare a static method with that name and signature"
                    : $"declare a public virtual method with that name and signature, or write .override {TypeNameFormatter.Pretty(iface)}::{shape.Name} inside the method that implements it";
                var mismatch = own.FirstOrDefault(s => s.Name == shape.Name && !shape.IsStatic && s.Matches(shape) && !s.IsVirtual);
                if (mismatch is not null)
                {
                    hint = $"{shape.Name} matches but is not virtual; add virtual to its header";
                }

                throw new ReplException($"{what} must implement {shape.Describe()} ({hint})");
            }
        }

        return implied;
    }

    private static List<MethodBase> ExplicitTargets(TypeDeclaration declaration) =>
        [.. declaration.Methods.SelectMany(m => m.Overrides.Select(o => o.Target)), .. declaration.Overrides.Select(o => o.Target)];

    private static bool TargetIs(MethodBase target, Shape shape, Type owner, TypeTable types) =>
        target.Name == shape.Name && target.IsStatic == shape.IsStatic
        && ReferenceEquals(TypeRelations.Definition(target.DeclaringType!), TypeRelations.Definition(owner))
        && TargetParameterCount(target, types) == shape.Parameters.Count;

    private static int TargetParameterCount(MethodBase target, TypeTable types)
    {
        if (types.TryGetMembers(target.DeclaringType!, out var own))
        {
            // A builder cannot list its parameters; the declaration behind it can.
            foreach (var (signature, builder, _) in own.Methods)
            {
                if (ReferenceEquals(builder, target))
                {
                    return signature.Parameters.Count;
                }
            }
        }

        try
        {
            return target.GetParameters().Length;
        }
        catch (NotSupportedException)
        {
            // An instantiation of a builder; the override parser already matched the shapes.
            return -1;
        }
    }

    private static MethodBase TargetOf(Type iface, Shape shape, TypeTable types, Func<Type, TypeDeclaration?> declarationOf)
    {
        var definition = TypeRelations.Definition(iface);
        if (types.TryGetMembers(definition, out var own))
        {
            var entry = own.FindMethods(shape.Name).FirstOrDefault(m => m.Signature.IsStatic == shape.IsStatic && m.Signature.Parameters.Count == shape.Parameters.Count);
            if (entry.Builder is not null)
            {
                return entry.Builder;
            }
        }

        _ = declarationOf;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        return iface.GetMethods(flags).First(m => m.Name == shape.Name && m.IsStatic == shape.IsStatic && m.GetParameters().Length == shape.Parameters.Count
            && m.GetParameters().Zip(shape.Parameters).All(p => TypeIdentity.Equal(p.First.ParameterType, p.Second)));
    }

    private static IEnumerable<Shape> ShapesOf(Type type, TypeTable types, Func<Type, TypeDeclaration?> declarationOf)
    {
        if (type.IsGenericParameter)
        {
            yield break;
        }

        var definition = TypeRelations.Definition(type);
        if (definition is System.Reflection.Emit.TypeBuilder)
        {
            // A prototype describes itself through its declaration, or through the table while
            // its block is still open.
            if (declarationOf(definition) is { } declaration)
            {
                foreach (var method in declaration.Methods)
                {
                    if (method.IsConstructor || method.IsTypeInitializer)
                    {
                        continue;
                    }

                    var signature = method.Signature;
                    yield return new Shape(
                        signature.Name,
                        signature.IsStatic,
                        TypeRelations.SubstituteFor(type, signature.ReturnType),
                        [.. signature.ParameterTypes.Select(p => TypeRelations.SubstituteFor(type, p))],
                        method.IsAbstract,
                        method.IsVirtual,
                        signature.Attributes.HasFlag(MethodAttributes.NewSlot),
                        type);
                }
            }
            else if (types.TryGetMembers(definition, out var own))
            {
                foreach (var (signature, _, declared) in own.Methods)
                {
                    if (!declared || signature.Name is ".ctor" or ".cctor")
                    {
                        continue;
                    }

                    yield return new Shape(
                        signature.Name,
                        signature.IsStatic,
                        TypeRelations.SubstituteFor(type, signature.ReturnType),
                        [.. signature.ParameterTypes.Select(p => TypeRelations.SubstituteFor(type, p))],
                        signature.Attributes.HasFlag(MethodAttributes.Abstract),
                        signature.Attributes.HasFlag(MethodAttributes.Virtual),
                        signature.Attributes.HasFlag(MethodAttributes.NewSlot),
                        type);
                }
            }

            yield break;
        }

        // A loaded type, session or framework, answers through reflection on its definition,
        // with the instantiation's arguments substituted in.
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var method in definition.GetMethods(flags))
        {
            yield return new Shape(
                method.Name,
                method.IsStatic,
                TypeRelations.SubstituteFor(type, method.ReturnType),
                [.. method.GetParameters().Select(p => TypeRelations.SubstituteFor(type, p.ParameterType))],
                method.IsAbstract,
                method.IsVirtual,
                method.Attributes.HasFlag(MethodAttributes.NewSlot),
                type);
        }
    }
}
