using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Binds parsed syntax through shared resolution, substitution, overload, and diagnostic rules.
/// </summary>
/// <remarks>
/// Binds syntax to symbols through a scope: the type a written name means, the member a reference
/// names among its overloads, with the substitutions, the tie-breaks, and the messages the
/// resolver has always used. The binder makes every decision; the scope only answers questions,
/// so the same decisions hold for an actual line and for a preview of one.
/// </remarks>
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
    /// Binds a type with optional placeholders for generic parameters whose declaring type is not yet known.
    /// </summary>
    /// <remarks>
    /// Binds a type, with a <c>!N</c> beyond what is in scope standing for <c>object</c> when
    /// <paramref name="lenientGenerics"/> is set: the first pass over a member reference, before
    /// the declaring type's own parameters are known.
    /// </remarks>
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

                if (!lenient)
                {
                    var parameters = scope.GenericParameterDeclarations(definition);
                    for (var i = 0; i < arguments.Count; i++)
                    {
                        if (i < parameters.Count && !GenericConstraints.Satisfies(parameters[i], arguments[i],
                            type => SymbolRelations.SubstituteTypeParameters(type, definition.Definition, arguments), scope))
                        {
                            throw new ReplException(
                                $"'{scope.Pretty(arguments[i])}' does not satisfy the constraints of '{parameters[i].Name}' "
                                + $"on '{scope.Pretty(definition)}'");
                        }
                    }
                }

                return TypeSymbol.Construct(definition, arguments);
            }

            case TypeSyntaxKind.Array:
            {
                var element = BindCore(syntax.Element!, scope, lenient);
                var (sizes, bounds) = ArraySignatureShape.Parse(syntax.Shape!);
                return syntax.IsVector ? TypeSymbol.SzArray(element) : TypeSymbol.Array(element, syntax.Rank, sizes, bounds);
            }

            case TypeSyntaxKind.ByRef:
                return TypeSymbol.ByRef(BindCore(syntax.Element!, scope, lenient));
            case TypeSyntaxKind.Pointer:
                return TypeSymbol.Pointer(BindCore(syntax.Element!, scope, lenient));
            case TypeSyntaxKind.FunctionPointer:
                return TypeSymbol.FunctionPointer(BindFunctionPointer(syntax.FunctionPointer!, scope, lenient));
            case TypeSyntaxKind.Modified:
                return TypeSymbol.Modified(BindCore(syntax.Element!, scope, lenient),
                    BindType(syntax.Modifier!, scope, lenient).Type, syntax.IsRequired);
            case TypeSyntaxKind.Pinned:
                return BindCore(syntax.Unwrapped, scope, lenient);
            default:
                throw new ReplException("expected a type");
        }
    }

    private static MethodSignatureSymbol BindFunctionPointer(SignatureSyntax syntax, IBindingScope scope, bool lenient)
    {
        var (managed, unmanaged, convention) = Conventions(syntax.ConventionWords);
        var returnType = BindCore(syntax.ReturnType, scope, lenient);
        var parameters = syntax.Parameters.Select(p => BindCore(p, scope, lenient)).ToList();
        var extensible = unmanaged && convention == System.Runtime.InteropServices.CallingConvention.Winapi;
        if (extensible)
        {
            SymbolSignatureProvider.StripModifiers(returnType, out _, out var modifiers);
            convention = FunctionPointerConvention.FromMarkers(modifiers);
        }

        return new MethodSignatureSymbol(managed, unmanaged, convention, returnType, parameters, syntax.SentinelIndex)
        {
            IsExtensibleUnmanaged = extensible,
        };
    }

    /// <summary>
    /// Binds a standalone signature, as <c>calli</c> takes it: a <c>...</c> in the parameters makes it vararg.
    /// </summary>
    /// <param name="syntax">The signature syntax.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The signature.</returns>
    /// <exception cref="ReplException">A type in the signature cannot be found.</exception>
    public static MethodSignatureSymbol BindSignature(SignatureSyntax syntax, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(scope);
        var (managed, unmanaged, convention) = Conventions(syntax.ConventionWords);
        var returnType = BindType(syntax.ReturnType, scope).Type;
        var parameters = syntax.Parameters.Select(p => BindType(p, scope).Type).ToList();
        if (syntax.SentinelIndex is not null)
        {
            managed = (managed & ~CallingConventions.Standard) | CallingConventions.VarArgs;
        }

        return new MethodSignatureSymbol(managed, unmanaged, convention, returnType, parameters, syntax.SentinelIndex);
    }

    /// <summary>
    /// Binds and validates an instruction operand according to its opcode and current scope.
    /// </summary>
    /// <remarks>
    /// Binds an instruction: the literal is checked against its opcode, a slot is found by name
    /// or index, and a type, member, field, or signature operand is bound in the scope.
    /// </remarks>
    /// <param name="syntax">The instruction syntax.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The bound instruction.</returns>
    /// <exception cref="ReplException">The operand is invalid or names nothing.</exception>
    public static BoundInstruction BindInstruction(InstructionSyntax syntax, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(scope);
        var op = syntax.Op;
        var opName = op.Name!;
        var text = syntax.Text;
        var operand = syntax.Operand;

        var implicitLocal = opName switch
        {
            "ldloc.0" or "stloc.0" => 0,
            "ldloc.1" or "stloc.1" => 1,
            "ldloc.2" or "stloc.2" => 2,
            "ldloc.3" or "stloc.3" => 3,
            _ => (int?)null,
        };
        if (implicitLocal is int localIndex)
        {
            if (localIndex >= scope.Locals.Count)
            {
                throw new ReplException($"local {localIndex} is not declared (declare it with .locals)");
            }

            RequireNoOperand(opName, operand.Text);
            return new BoundInstruction(op, text, BoundOperand.None, localIndex, null);
        }

        var implicitArgument = opName switch
        {
            "ldarg.0" => 0,
            "ldarg.1" => 1,
            "ldarg.2" => 2,
            "ldarg.3" => 3,
            _ => (int?)null,
        };
        if (implicitArgument is int argumentIndex)
        {
            if (argumentIndex >= scope.Arguments.Count)
            {
                throw new ReplException(scope.ThisIndex >= 0 || scope.Arguments.Count > 0 && scope.Arguments[0].Name is null
                    ? $"argument {argumentIndex} is not declared (the parameters are in the method header)"
                    : $"argument {argumentIndex} is not declared (declare it with .args)");
            }

            RequireNoOperand(opName, operand.Text);
            return new BoundInstruction(op, text, BoundOperand.None, null, argumentIndex);
        }

        if (opName == "arglist")
        {
            RequireNoOperand(opName, operand.Text);
            return new BoundInstruction(op, text, BoundOperand.None, null, null);
        }

        switch (operand.Kind)
        {
            case OperandSyntaxKind.None:
                RequireNoOperand(opName, operand.Text);
                return new BoundInstruction(op, text, BoundOperand.None, null, null);

            case OperandSyntaxKind.Integer:
                if (op.OperandType == System.Reflection.Emit.OperandType.ShortInlineI)
                {
                    var v = LiteralParser.ParseInteger(operand.Text, opName);
                    if (opName == "ldc.i4.s")
                    {
                        if (v is < sbyte.MinValue or > sbyte.MaxValue)
                        {
                            throw new ReplException($"{v} does not fit ldc.i4.s (int8); use ldc.i4");
                        }

                        return Literal(OperandKind.SByte, (sbyte)v);
                    }

                    if (v is < byte.MinValue or > byte.MaxValue)
                    {
                        throw new ReplException($"{v} does not fit an unsigned byte operand");
                    }

                    return Literal(OperandKind.Byte, (byte)v);
                }

                if (op.OperandType == System.Reflection.Emit.OperandType.InlineI)
                {
                    var v = LiteralParser.ParseInteger(operand.Text, opName);
                    if (v is < int.MinValue or > uint.MaxValue)
                    {
                        throw new ReplException($"{v} does not fit int32; use ldc.i8");
                    }

                    return Literal(OperandKind.Int32, unchecked((int)v));
                }

                return Literal(OperandKind.Int64, LiteralParser.ParseInteger(operand.Text, opName));

            case OperandSyntaxKind.Float:
                return op.OperandType == System.Reflection.Emit.OperandType.ShortInlineR
                    ? Literal(OperandKind.Single, LiteralParser.ParseFloat32(operand.Text, opName))
                    : Literal(OperandKind.Double, LiteralParser.ParseFloat(operand.Text, opName));

            case OperandSyntaxKind.String:
                if (operand.Text.Length == 0)
                {
                    throw new ReplException("ldstr needs a string operand, e.g. ldstr \"hello\"");
                }

                return Literal(OperandKind.String, LiteralParser.ParseString(operand.Text));

            case OperandSyntaxKind.Label:
                return Literal(OperandKind.Label, operand.Text);

            case OperandSyntaxKind.Labels:
                return Literal(OperandKind.Labels, operand.Labels.ToArray());

            case OperandSyntaxKind.Variable:
                if (operand.IsArgument)
                {
                    var index = BindArgument(operand.Text, scope);
                    return new BoundInstruction(op, text, new BoundOperand { Kind = OperandKind.Argument, Value = index }, null, index);
                }
                else
                {
                    var index = BindLocal(operand.Text, scope);
                    return new BoundInstruction(op, text, new BoundOperand { Kind = OperandKind.Local, Value = index }, index, null);
                }

            case OperandSyntaxKind.Type:
                return new BoundInstruction(op, text, new BoundOperand
                {
                    Kind = OperandKind.Type,
                    Type = BindType(operand.Type!,
                    scope).Type
                }, null, null);

            case OperandSyntaxKind.Member:
                return new BoundInstruction(op, text, new BoundOperand
                {
                    Kind = OperandKind.Method,
                    Method = BindMethodReference(
                    operand.Member!, scope, op == System.Reflection.Emit.OpCodes.Newobj)
                }, null, null);

            case OperandSyntaxKind.Field:
                return new BoundInstruction(op, text, new BoundOperand
                {
                    Kind = OperandKind.Field,
                    Field = BindFieldReference(
                    operand.Member!, scope)
                }, null, null);

            case OperandSyntaxKind.Token:
            {
                var token = operand.IsMethodToken
                    ? new BoundOperand
                    {
                        Kind = OperandKind.Token,
                        Method = BindMethodReference(operand.Member!, scope,
                        wantConstructor: false)
                    }
                    : operand.IsFieldToken
                        ? new BoundOperand { Kind = OperandKind.Token, Field = BindFieldReference(operand.Member!, scope) }
                        : new BoundOperand { Kind = OperandKind.Token, Type = BindType(operand.Type!, scope).Type };
                return new BoundInstruction(op, text, token, null, null);
            }

            case OperandSyntaxKind.Signature:
                return new BoundInstruction(op, text, new BoundOperand
                {
                    Kind = OperandKind.Signature,
                    Signature = BindSignature(
                    operand.Signature!, scope)
                }, null, null);

            default:
                throw new ReplException($"unsupported operand type {op.OperandType} for '{opName}'");
        }

        BoundInstruction Literal(OperandKind kind, object value) => new(op, text, new BoundOperand { Kind = kind, Value = value }, null,
            null);
    }

    private static void RequireNoOperand(string opName, string operandText)
    {
        if (operandText.Length > 0)
        {
            throw new ReplException($"'{opName}' takes no operand");
        }
    }

    /// <summary>
    /// Finds a local written as a name or an index.
    /// </summary>
    /// <param name="operand">The operand text.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The local index.</returns>
    /// <exception cref="ReplException">No such local is declared.</exception>
    public static int BindLocal(string operand, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(scope);
        var locals = scope.Locals;
        if (operand.Length == 0)
        {
            throw new ReplException("expected a local name or index");
        }

        if (int.TryParse(operand, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
            out var index))
        {
            if (index < 0 || index >= locals.Count)
            {
                throw new ReplException($"local {index} is not declared ({locals.Count} declared; use .locals)");
            }

            return index;
        }

        var name = InstructionParser.Unquote(operand);
        for (var i = 0; i < locals.Count; i++)
        {
            if (locals[i].Name == name)
            {
                return i;
            }
        }

        throw new ReplException(locals.Count == 0
            ? $"no local '{name}'; declare one with: .locals init (int32 {name})"
            : $"no local '{name}'; declared: {string.Join(", ", locals.Select((l, i) => $"{i}:{l.Name ?? "?"}"))}");
    }

    /// <summary>
    /// Finds an argument written as a name or an index.
    /// </summary>
    /// <param name="operand">The operand text.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The argument index.</returns>
    /// <exception cref="ReplException">No such argument is declared.</exception>
    public static int BindArgument(string operand, IBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(scope);
        var arguments = scope.Arguments;
        if (operand.Length == 0)
        {
            throw new ReplException("expected an argument name or index");
        }

        if (int.TryParse(operand, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
            out var index))
        {
            if (index < 0 || index >= arguments.Count)
            {
                throw new ReplException($"argument {index} is not declared ({arguments.Count} declared; use .args)");
            }

            return index;
        }

        var name = InstructionParser.Unquote(operand);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Name == name)
            {
                return i;
            }
        }

        throw new ReplException(arguments.Count == 0
            ? $"no argument '{name}'; declare one with: .args (int32 {name} = 0)"
            : $"no argument '{name}'; declared: {string.Join(", ", arguments.Select((a, i) => $"{i}:{a.Name ?? "?"}"))}");
    }

    /// <summary>
    /// The calling convention the words of a signature spell.
    /// </summary>
    /// <param name="words">The ordered calling-convention words, such as <c>instance</c>, <c>vararg</c>, or <c>cdecl</c>.</param>
    /// <returns>The managed convention, whether the signature is unmanaged, and the unmanaged convention.</returns>
    public static (CallingConventions Managed, bool IsUnmanaged, System.Runtime.InteropServices.CallingConvention Unmanaged) Conventions(
        IEnumerable<string> words)
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
        BindType(syntax.DeclaringType!, scope);
        var returnType = syntax.ReturnType is null ? null : BindType(syntax.ReturnType, memberScope).Type;

        IReadOnlyList<TypeSymbol>? parameterTypes = null;
        IReadOnlyList<TypeSymbol>? optionalTypes = null;
        if (syntax.Parameters is not null)
        {
            parameterTypes = [.. syntax.FixedParameters.Select(p => BindType(p, memberScope).Type)];
            optionalTypes = syntax.OptionalParameters is null ? null : [.. syntax.OptionalParameters.Select(p => BindType(p,
                memberScope).Type)];
        }

        var explicitMethodArguments = syntax.GenericArguments is null ? null : methodArguments;
        if (scope.TryGetDeclaration(declaring, out var own))
        {
            return BindOwnMethod(own, declaring, scope, syntax, parameterTypes, returnType, syntax.ExplicitInstance, wantConstructor
                || name is ".ctor" or ".cctor", explicitMethodArguments, optionalTypes);
        }

        if (wantConstructor || name is ".ctor" or ".cctor")
        {
            return new BoundMethod(BindConstructor(declaring, name, parameterTypes, scope), null, optionalTypes);
        }

        var method = BindLoadedMethod(declaring, syntax, parameterTypes, explicitMethodArguments, returnType, syntax.ExplicitInstance,
            syntax.IsVarArg, scope);
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
                var suggestion = declared.Length == 0 ? "" : ConfirmedFieldSuggestion(scope, syntax, name, own.Fields.Select(f => f.Name));
                throw new ReplException(declared.Length == 0
                    ? $"no field '{name}' on {scope.Pretty(declaring)} (declare it with .field first)"
                    : $"no field '{name}' on {scope.Pretty(declaring)}{suggestion}; fields: {declared}");
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
        var fieldSuggestion = names.Length == 0 ? "" : ConfirmedFieldSuggestion(scope, syntax, name, EligibleFieldNames(scope, declaring));
        throw new ReplException(names.Length == 0
            ? $"no field '{name}' on {scope.Pretty(declaring)}"
            : $"no field '{name}' on {scope.Pretty(declaring)}{fieldSuggestion}; fields: {names}");
    }

    /// <summary>
    /// Lists accessible method names in stable order for typo suggestions.
    /// </summary>
    /// <remarks>
    /// The names of the methods a context may call on a type, for a suggestion: every method the
    /// type offers whose access the context passes, in a stable order.
    /// </remarks>
    private static IEnumerable<string> EligibleMethodNames(IBindingScope scope, TypeSymbol declaring)
    {
        var facts = AccessFacts.From(scope);
        return scope.AllMethods(declaring)
            .Where(m => !m.IsConstructor && MemberEligibility.AccessProblem(m, scope.Access, facts) is null)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> EligibleFieldNames(IBindingScope scope, TypeSymbol declaring)
    {
        var facts = AccessFacts.From(scope);
        return scope.Fields(declaring)
            .Where(f => MemberEligibility.AccessProblem(f, scope.Access, facts) is null)
            .Select(f => f.Name)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// Lists declared and inherited member names available to an open type's access context.
    /// </summary>
    /// <remarks>
    /// The names a member of a type being written could have meant: the declared members, then
    /// what the base chain offers to this context.
    /// </remarks>
    private static IEnumerable<string> OwnAndInheritedNames(IDeclarationMembers own, TypeSymbol declaring, IBindingScope scope)
    {
        var names = own.Methods.Where(m => m.IsDeclared && !m.IsConstructor).Select(m => m.Name).ToList();
        for (var current = own.BaseType; current is not null; current = scope.BaseOf(current))
        {
            if (scope.TryGetDeclaration(current, out var baseOwn))
            {
                names.AddRange(baseOwn.Methods.Where(m => m.IsDeclared && !m.IsConstructor).Select(m => m.Name));
                continue;
            }

            if (current.DefinitionOrSelf.Definition.IsDeclaration)
            {
                continue;
            }

            names.AddRange(EligibleMethodNames(scope, current));
            break;
        }

        _ = declaring;
        return names.Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// Suggests a corrected method name only when its replacement reference binds without loading dependencies.
    /// </summary>
    /// <remarks>
    /// The did-you-mean for a mistyped method name, when the reference with the nearest name in its
    /// place binds in a scope that loads nothing; empty otherwise.
    /// </remarks>
    private static string ConfirmedSuggestion(IBindingScope scope, MemberSyntax syntax, string name, IEnumerable<string> pool,
        bool wantConstructor)
    {
        var pure = scope.ForSuggestions(out var lease);
        using (lease)
        {
            var nearest = NameSuggestions.Nearest(name, pool, candidate =>
            {
                try
                {
                    BindMethodReference(syntax with { Name = candidate }, pure, wantConstructor);
                    return true;
                }
                catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
                {
                    return false;
                }
            });
            return nearest is null ? "" : NameSuggestions.Parenthetical(nearest);
        }
    }

    private static string ConfirmedFieldSuggestion(IBindingScope scope, MemberSyntax syntax, string name, IEnumerable<string> pool)
    {
        var pure = scope.ForSuggestions(out var lease);
        using (lease)
        {
            var nearest = NameSuggestions.Nearest(name, pool, candidate =>
            {
                try
                {
                    BindFieldReference(syntax with { Name = candidate }, pure);
                    return true;
                }
                catch (Exception exception) when (ReplRecovery.IsRecoverable(exception))
                {
                    return false;
                }
            });
            return nearest is null ? "" : NameSuggestions.Parenthetical(nearest);
        }
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
            var suggestion = methods.Count == 0 ? "" : ConfirmedSuggestion(scope, syntax, name, methods.Select(m => m.Name),
                wantConstructor: false);
            var defined = string.Join(", ", methods.Select(m => SymbolRenderer.DescribeSignature(m, scope.Pretty)));
            throw new ReplException(methods.Count == 0
                ? $"no method '{name}' in the session (define one with .method, or write Type::{name}(...) for a framework method)"
                : $"no method '{name}' in the session{suggestion}; defined: {defined}  (define one with .method)");
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
                throw new ReplException(
                    $"no method {name}({string.Join(", ", types.Select(scope.Pretty))}) in the session; "
                    + $"defined: {SymbolRenderer.DescribeSignature(signature, scope.Pretty)}");
            }
        }

        return new BoundMethod(signature, null, null);
    }

    private static BoundMethod BindOwnMethod(IDeclarationMembers own, TypeSymbol declaring, IBindingScope scope, MemberSyntax syntax,
        IReadOnlyList<TypeSymbol>? parameterTypes, TypeSymbol? returnType, bool explicitInstance, bool wantConstructor,
        IReadOnlyList<TypeSymbol>? methodArguments, IReadOnlyList<TypeSymbol>? optionalTypes)
    {
        var name = syntax.Name;
        var instantiated = declaring.Kind == TypeSymbolKind.Constructed;
        var arity = methodArguments?.Count ?? 0;
        var candidates = own.FindMethods(name)
            .Where(m => m.GenericParameters.Count == arity)
            .Where(method => arity == 0 || GenericConstraints.SatisfiesMethod(method, declaring, methodArguments!, scope))
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
                throw new ReplException(
                    $"{scope.Pretty(declaring)}::{name} returns {scope.Pretty(effective.ReturnType)}, not {scope.Pretty(returnType)}");
            }

            if (wantConstructor && signature.Name != ".ctor")
            {
                throw new ReplException($"newobj needs a constructor; {name} is a method");
            }

            return new BoundMethod(effective, signature, optionalTypes);
        }

        if (candidates.Count == 0 && !wantConstructor && own.BaseType is { } baseType && BindInherited(baseType, scope, syntax,
            parameterTypes, returnType, explicitInstance, methodArguments, optionalTypes) is { } inherited)
        {
            return inherited;
        }

        if (candidates.Count == 0 && arity == 0 && returnType is not null && parameterTypes is not null && own.CanDefineForward
            && !instantiated && !scope.Inspecting)
        {
            // A member referenced before its declaration: the signature is taken at its word and
            // checked when the type closes, which is what lets members call each other in any order.
            var forward = new MethodSymbol
            {
                Definition = DefinitionId.None,
                Source = MethodSymbolSource.Forward,
                DeclaringType = declaring,
                Name = name,
                Attributes = wantConstructor ? MethodAttributes.Public | MethodAttributes.SpecialName
                    | MethodAttributes.RTSpecialName : explicitInstance ? MethodAttributes.Public : MethodAttributes.Public
                    | MethodAttributes.Static,
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
            var names = string.Join(", ", own.Methods.Where(m => m.IsDeclared).Select(m => SymbolRenderer.DescribeSignature(m,
                scope.Pretty)).Take(12));
            var suggestion = names.Length == 0 ? "" : ConfirmedSuggestion(scope, syntax, name, OwnAndInheritedNames(own, declaring, scope),
                wantConstructor);
            throw new ReplException(names.Length == 0
                ? $"no method '{name}' on {scope.Pretty(declaring)} yet "
                    + "(declare it, or reference it with its full signature to declare it later)"
                : $"no method '{name}' on {scope.Pretty(declaring)}{suggestion}; methods: {names}");
        }

        if (candidates.Count == 0)
        {
            throw new ReplException(
                $"no overload {scope.Pretty(declaring)}::{name}({Signature(parameterTypes, scope)}); candidates:\n"
                + string.Join("\n", known.Select(k => "    " + SymbolRenderer.DescribeMember(k, scope.Pretty))));
        }

        throw new ReplException(
            $"ambiguous: {scope.Pretty(declaring)}::{name}; give parameter types. candidates:\n"
            + string.Join("\n", candidates.Select(c => "    " + SymbolRenderer.DescribeMember(c.Declared, scope.Pretty))));
    }

    private static string NoConstructor(TypeSymbol declaring, IBindingScope scope)
    {
        var valueType = declaring.IsValueTypeShape;
        return $"{(valueType ? "struct" : "class")} {scope.Pretty(declaring)} declares no constructor "
            + $"(add a .method public instance void .ctor(...) to it{(valueType ? ", or use initobj" : "")})";
    }

    /// <summary>
    /// Finds members inherited from declared or loaded base types.
    /// </summary>
    /// <remarks>
    /// Finds a member a type being written inherits: from a base still being written through its
    /// declarations, from a loaded base through its members.
    /// </remarks>
    private static BoundMethod? BindInherited(TypeSymbol baseType, IBindingScope scope, MemberSyntax syntax,
        IReadOnlyList<TypeSymbol>? parameterTypes, TypeSymbol? returnType, bool explicitInstance,
        IReadOnlyList<TypeSymbol>? methodArguments, IReadOnlyList<TypeSymbol>? optionalTypes)
    {
        var name = syntax.Name;
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
                var method = BindLoadedMethod(current, syntax, parameterTypes, methodArguments, returnType, explicitInstance,
                    optionalTypes is not null, scope);
                return new BoundMethod(method, null, optionalTypes);
            }
            catch (ReplException)
            {
                // Not declared there; the next base may have it.
            }
        }

        return null;
    }

    private static BoundMethod BindGenericDefinition(MemberSyntax syntax, TypeSymbol declaring, IReadOnlyList<TypeSymbol> typeArguments,
        IBindingScope scope)
    {
        var name = syntax.Name;
        var arity = syntax.GenericArity!.Value;
        var matches = new List<MethodSymbol>();
        foreach (var candidate in scope.Methods(declaring, name).Where(m => m.IsGenericDefinition && m.Arity == arity))
        {
            var candidateScope = scope.WithGenerics(new SymbolGenericContext(typeArguments,
                [.. candidate.GenericParameters.Select(p => p.AsType)]));
            BindType(syntax.DeclaringType!, scope);
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

        if (matches.Count == 0)
        {
            var suggestion = scope.Methods(declaring, name).Count == 0
                ? ConfirmedSuggestion(scope, syntax, name, EligibleMethodNames(scope, declaring)
                    .Where(n => scope.Methods(declaring, n).Any(m => m.IsGenericDefinition && m.Arity == arity)), wantConstructor: false)
                : "";
            throw new ReplException($"no generic method '{name}' with {arity} type parameter(s) "
                + $"and those parameters on {scope.Pretty(declaring)}{suggestion}");
        }

        return matches.Count switch
        {
            1 => new BoundMethod(matches[0], null, null),
            _ => throw new ReplException($"ambiguous: {scope.Pretty(declaring)}::{name}<[{arity}]>; give parameter types"),
        };
    }

    private static MethodSymbol BindConstructor(TypeSymbol declaring, string name, IReadOnlyList<TypeSymbol>? parameterTypes,
        IBindingScope scope)
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
            throw new ReplException(
                $"no constructor {scope.Pretty(declaring)}({Signature(parameterTypes, scope)}); candidates:\n"
                + Candidates(constructors, scope));
        }

        throw new ReplException($"ambiguous constructor for {scope.Pretty(declaring)}; candidates:\n{Candidates(matched, scope)}");
    }

    private static MethodSymbol BindLoadedMethod(
        TypeSymbol declaring,
        MemberSyntax syntax,
        IReadOnlyList<TypeSymbol>? parameterTypes,
        IReadOnlyList<TypeSymbol>? methodGenericArguments,
        TypeSymbol? returnType,
        bool explicitInstance,
        bool isVarArg,
        IBindingScope scope)
    {
        var name = syntax.Name;
        if (scope.RequiresDefinitionLookup(declaring) && methodGenericArguments is null)
        {
            var candidates = scope.Methods(declaring, name)
                .Where(m => !m.IsGenericDefinition)
                .Where(m => parameterTypes is null || ParametersMatch(m, parameterTypes))
                .ToList();
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            var suggestion = candidates.Count == 0 && scope.Methods(declaring, name).Count == 0
                ? ConfirmedSuggestion(scope, syntax, name, EligibleMethodNames(scope, declaring), wantConstructor: false)
                : "";
            throw new ReplException(candidates.Count == 0
                ? $"no method '{name}' with those parameters on {scope.Pretty(declaring)}{suggestion}"
                : $"ambiguous: {scope.Pretty(declaring)}::{name}; give parameter types");
        }

        var methods = scope.Methods(declaring, name);
        if (methods.Count == 0)
        {
            throw new ReplException(
                $"no method '{name}' on {scope.Pretty(declaring)}"
                + ConfirmedSuggestion(scope, syntax, name, EligibleMethodNames(scope, declaring), wantConstructor: false));
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
            throw new ReplException(
                $"no overload {scope.Pretty(declaring)}::{name}({Signature(parameterTypes, scope)}); candidates:\n"
                + Candidates(methods, scope));
        }

        throw new ReplException(
            $"ambiguous: {scope.Pretty(declaring)}::{name}; give parameter types. candidates:\n{Candidates(candidateSet, scope)}");
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
