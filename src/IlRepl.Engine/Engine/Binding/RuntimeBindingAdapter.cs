using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Projects bound symbols onto their exact runtime objects for emission and stack simulation.
/// </summary>
/// <remarks>
/// Projects what the binder bound in a <see cref="RuntimeBindingScope"/> onto the objects the
/// emitter and the stack model take: a <see cref="Type"/>, a <see cref="ResolvedMethod"/>, a
/// <see cref="FieldInfo"/>. The projection is by the exact identity the scope recorded, never by a
/// fresh name lookup, so a bound reference means the same member when it is emitted.
/// </remarks>
public sealed class RuntimeBindingAdapter
{
    private readonly RuntimeBindingScope _scope;

    /// <summary>
    /// Initializes an adapter over the scope a reference was bound in.
    /// </summary>
    /// <param name="scope">The scope.</param>
    public RuntimeBindingAdapter(RuntimeBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope = scope;
    }

    /// <summary>
    /// The runtime type of a bound type symbol.
    /// </summary>
    /// <param name="type">The symbol.</param>
    /// <returns>The type.</returns>
    public Type ToType(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _scope.TypeOf(type);
    }

    /// <summary>
    /// Reconstructs a runtime type from registered definitions without a binding scope.
    /// </summary>
    /// <remarks>
    /// The runtime type a symbol stands for, built from the definitions the runtime registry
    /// remembers, with no scope: for symbols that were imported from runtime types and are
    /// projected back outside a binding.
    /// </remarks>
    /// <param name="symbol">The symbol.</param>
    /// <returns>The type.</returns>
    /// <exception cref="InvalidOperationException">The symbol names a definition no runtime object stands for.</exception>
    public static Type Materialize(TypeSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        switch (symbol.Kind)
        {
            case TypeSymbolKind.Primitive:
                return CilPrimitives.TypeOf(symbol.Keyword!);
            case TypeSymbolKind.Named:
                return RuntimeDefinitions.TypeOf(symbol.Definition) ?? throw new InvalidOperationException(
                    $"no runtime type stands for {SymbolRenderer.IlPath(symbol)}");
            case TypeSymbolKind.Constructed:
                return Materialize(symbol.Element!).MakeGenericType([.. symbol.Arguments.Select(Materialize)]);
            case TypeSymbolKind.TypeParameter:
            case TypeSymbolKind.MethodParameter:
                return RuntimeDefinitions.ParameterOf(symbol.Owner, symbol.Kind == TypeSymbolKind.MethodParameter, symbol.Position)
                    ?? throw new InvalidOperationException(
                        $"no runtime type stands for the generic parameter {SymbolRenderer.Pretty(symbol)}");
            case TypeSymbolKind.SzArray:
                return Materialize(symbol.Element!).MakeArrayType();
            case TypeSymbolKind.Array:
                return Materialize(symbol.Element!).MakeArrayType(symbol.Rank);
            case TypeSymbolKind.ByRef:
                return Materialize(symbol.Element!).MakeByRefType();
            case TypeSymbolKind.Pointer:
                return Materialize(symbol.Element!).MakePointerType();
            case TypeSymbolKind.FunctionPointer:
                return typeof(nint);
            case TypeSymbolKind.Modified:
            case TypeSymbolKind.Pinned:
                return Materialize(symbol.Element!);
            default:
                throw new InvalidOperationException($"no runtime type for a {symbol.Kind} symbol");
        }
    }

    /// <summary>
    /// The runtime types of bound type symbols.
    /// </summary>
    /// <param name="types">The symbols.</param>
    /// <returns>The types, in order.</returns>
    public Type[] ToTypes(IEnumerable<TypeSymbol> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        return [.. types.Select(ToType)];
    }

    /// <summary>
    /// The method reference the emitter takes for a bound member.
    /// </summary>
    /// <param name="bound">The bound member.</param>
    /// <returns>The resolved method.</returns>
    /// <exception cref="InvalidOperationException">The member was not bound in this adapter's scope.</exception>
    public ResolvedMethod ToResolvedMethod(BoundMethod bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        var method = bound.Method;
        var optional = bound.OptionalParameterTypes is null ? null : ToTypes(bound.OptionalParameterTypes);
        switch (_scope.PayloadOf(method))
        {
            case MethodSignature session:
                return new ResolvedMethod(session);
            case RuntimeDeclaredMember declared:
            {
                var declaringType = ToType(method.DeclaringType!);
                var effective = declared.Signature with
                {
                    ReturnType = ToType(method.ReturnType),
                    ExactSymbol = declared.Signature.ExactSymbol is null ? null : method,
                    Parameters = [.. declared.Signature.Parameters.Select((p, i)
                            => p with
                            {
                                Type = ToType(method.Parameters[i].Type),
                                ExactType = declared.Signature.Parameters[i].ExactType is null
                                    ? null : method.Parameters[i].Type,
                            })],
                };
                return new ResolvedMethod(declared.Builder, effective, declaringType)
                {
                    OptionalParameterTypesOverride = optional,
                    DeclaredDefinition = bound.Definition is null ? null : declared.Signature,
                    GenericArguments = method.GenericArguments.Count > 0 ? ToTypes(method.GenericArguments) : null,
                    ExactGenericArguments = ExactGenericArguments(bound),
                };
            }

            case RuntimeDefinitionMember definition:
            {
                var declaringType = ToType(method.DeclaringType!);
                var mapped = definition.Method switch
                {
                    ConstructorInfo constructor => (MethodBase)TypeBuilder.GetConstructor(declaringType, constructor),
                    MethodInfo info => TypeBuilder.GetMethod(declaringType, info),
                    _ => throw new InvalidOperationException("a definition member is a method or a constructor"),
                };
                var arguments = ToTypes(method.GenericArguments);
                if (arguments.Length > 0)
                {
                    mapped = ((MethodInfo)mapped).MakeGenericMethod(arguments);
                }

                return new ResolvedMethod(mapped, ToSignature(method), declaringType)
                {
                    OptionalParameterTypesOverride = optional,
                    GenericArguments = arguments,
                    ExactGenericArguments = ExactGenericArguments(bound),
                    DeclaredDefinition = ToSignature(RuntimeSymbolImporter.Import(definition.Method)),
                };
            }

            case MethodBase runtime:
                return new ResolvedMethod(runtime, optional)
                {
                    ExactGenericArguments = ExactGenericArguments(bound),
                };
            default:
                throw new InvalidOperationException($"{SymbolRenderer.Describe(method)} was not bound in this scope");
        }
    }

    private MethodSignature ToSignature(MethodSymbol method) => new(method.Name, ToType(method.ReturnType),
        [.. method.Parameters.Select(parameter => new ArgumentDeclaration(ToType(parameter.Type), parameter.Name, null, "")
        {
            ExactType = RuntimeSymbolTypes.RequiresExact(parameter.Type) ? parameter.Type : null,
            Attributes = parameter.Attributes,
            RequiredModifiers = ToTypes(parameter.RequiredModifiers),
            OptionalModifiers = ToTypes(parameter.OptionalModifiers),
        })])
    {
        ExactSymbol = RequiresExact(method) ? method : null,
        Attributes = method.Attributes,
        ImplAttributes = method.ImplAttributes,
        CallingConvention = method.CallingConvention,
        ReturnRequiredModifiers = ToTypes(method.ReturnRequiredModifiers),
        ReturnOptionalModifiers = ToTypes(method.ReturnOptionalModifiers),
        TypeParameters = [.. method.GenericParameters.Select(parameter =>
            new GenericParameterDeclaration(parameter.Name, parameter.Attributes, ToTypes(parameter.Constraints)))],
    };

    private static bool RequiresExact(MethodSymbol method) => RuntimeSymbolTypes.RequiresExact(method.ReturnType)
        || method.Parameters.Any(parameter => RuntimeSymbolTypes.RequiresExact(parameter.Type));

    private static IReadOnlyList<TypeSymbol>? ExactGenericArguments(BoundMethod method) =>
        method.ExactGenericArguments.Any(RuntimeSymbolTypes.RequiresExact) ? method.ExactGenericArguments : null;

    /// <summary>
    /// The <c>calli</c> signature the emitter takes for a bound signature.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <returns>The calli signature.</returns>
    public CalliSignature ToCalliSignature(MethodSignatureSymbol signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return new CalliSignature(
            signature.IsUnmanaged,
            signature.UnmanagedConvention,
            signature.ManagedConvention,
            ToType(signature.ReturnType),
            ToTypes(signature.FixedParameters),
            signature.OptionalParameters is null ? null : ToTypes(signature.OptionalParameters))
        {
            ExactSymbol = signature,
        };
    }

    /// <summary>
    /// The instruction the emitter and the stack model take for a bound instruction.
    /// </summary>
    /// <param name="bound">The bound instruction.</param>
    /// <returns>The instruction.</returns>
    public Instruction ToInstruction(BoundInstruction bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        var operand = bound.Operand;
        var value = operand.Kind switch
        {
            OperandKind.None => null,
            OperandKind.Type => ToType(operand.Type!),
            OperandKind.Method => ToResolvedMethod(operand.Method!),
            OperandKind.Field => ToField(operand.Field!),
            OperandKind.Signature => ToCalliSignature(operand.Signature!),
            OperandKind.Token => operand.Type is not null ? ToType(operand.Type) : operand.Field is not null ? ToField(
                operand.Field) : ToResolvedMethod(operand.Method!),
            _ => operand.Value,
        };
        return new Instruction
        {
            Op = bound.Op,
            Text = bound.Text,
            Kind = operand.Kind,
            Operand = value,
            ExactTypeOperand = operand.ExactType is { } type && RuntimeSymbolTypes.RequiresExact(type) ? type : null,
            LocalIndex = bound.LocalIndex,
            ArgumentIndex = bound.ArgumentIndex,
        };
    }

    /// <summary>
    /// The field the emitter takes for a bound field.
    /// </summary>
    /// <param name="field">The bound field.</param>
    /// <returns>The field.</returns>
    /// <exception cref="InvalidOperationException">The field was not bound in this adapter's scope.</exception>
    public FieldInfo ToField(FieldSymbol field)
    {
        ArgumentNullException.ThrowIfNull(field);
        switch (_scope.PayloadOf(field))
        {
            case RuntimeDeclaredField declared:
            {
                var declaringType = ToType(field.DeclaringType);
                var runtime = declaringType.IsGenericType && !declaringType.IsGenericTypeDefinition
                    && declared.Builder is FieldBuilder fieldBuilder
                    ? TypeBuilder.GetField(declaringType, fieldBuilder)
                    : declared.Builder;
                RuntimeFieldSignatures.Record(runtime, field.FieldType);
                return runtime;
            }

            case RuntimeDefinitionField definition:
                return TypeBuilder.GetField(ToType(field.DeclaringType), definition.Field);
            case FieldInfo runtime:
                return runtime;
            default:
                throw new InvalidOperationException($"{field} was not bound in this scope");
        }
    }
}
