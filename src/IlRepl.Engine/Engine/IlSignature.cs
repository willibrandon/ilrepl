
namespace IlRepl.Engine;

/// <summary>
/// A type as a metadata signature encodes it, kept whole: custom modifiers in position, array
/// shapes with their sizes and bounds, function pointers with their own signatures, pinning,
/// generic parameters by position. Reflection cannot represent all of this, so the listing
/// renders from here and only the stack simulator works from the projected <see cref="Type"/>.
/// </summary>
public sealed record IlSignature
{
    private IlSignature(IlSignatureKind kind)
    {
        Kind = kind;
    }

    /// <summary>
    /// The shape.
    /// </summary>
    public IlSignatureKind Kind { get; }

    /// <summary>
    /// The runtime type for a primitive, a named type, the definition of a generic instance, or
    /// a generic parameter the context could name; null when resolution failed or does not apply.
    /// </summary>
    public Type? Resolved { get; init; }

    /// <summary>
    /// The spelling of a named type that did not resolve, as <c>[Assembly]Namespace.Name</c>, or
    /// <c>Outer/Inner</c> for a nested one; null otherwise.
    /// </summary>
    public string? UnresolvedName { get; init; }

    /// <summary>
    /// True when a named type is a value type, from the signature's <c>valuetype</c> marker or the resolved type.
    /// </summary>
    public bool IsValueType { get; init; }

    /// <summary>
    /// The element of an array, pointer, byref, pinned, or modified type; the definition of a generic instance.
    /// </summary>
    public IlSignature? Element { get; init; }

    /// <summary>
    /// The arguments of a generic instance.
    /// </summary>
    public IReadOnlyList<IlSignature> Arguments { get; init; } = [];

    /// <summary>
    /// The rank of a general array.
    /// </summary>
    public int Rank { get; init; }

    /// <summary>
    /// The sizes a general array declares, leading dimensions first; shorter than the rank when some are open.
    /// </summary>
    public IReadOnlyList<int> Sizes { get; init; } = [];

    /// <summary>
    /// The lower bounds a general array declares, leading dimensions first.
    /// </summary>
    public IReadOnlyList<int> LowerBounds { get; init; } = [];

    /// <summary>
    /// The modifier type of a modified type.
    /// </summary>
    public IlSignature? Modifier { get; init; }

    /// <summary>
    /// True for <c>modreq</c>, false for <c>modopt</c>.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// The signature of a function pointer.
    /// </summary>
    public IlMethodSignature? Method { get; init; }

    /// <summary>
    /// The position of a generic parameter.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// The primitive keyword, for <see cref="IlSignatureKind.Primitive"/>.
    /// </summary>
    public string? Keyword { get; init; }

    /// <summary>
    /// The vararg sentinel.
    /// </summary>
    public static IlSignature Sentinel { get; } = new(IlSignatureKind.Sentinel);

    /// <summary>
    /// A primitive type.
    /// </summary>
    /// <param name="type">The runtime type.</param>
    /// <param name="keyword">Its IL keyword.</param>
    /// <returns>The signature.</returns>
    public static IlSignature Primitive(Type type, string keyword) => new(IlSignatureKind.Primitive) { Resolved = type, Keyword = keyword, IsValueType = type.IsValueType };

    /// <summary>
    /// A named type that resolved.
    /// </summary>
    /// <param name="type">The runtime type.</param>
    /// <returns>The signature.</returns>
    public static IlSignature Named(Type type) => new(IlSignatureKind.Named) { Resolved = type, IsValueType = type.IsValueType };

    /// <summary>
    /// A named type that did not resolve, spelled from its metadata row.
    /// </summary>
    /// <param name="name">The <c>[Assembly]Namespace.Name</c> spelling.</param>
    /// <param name="isValueType">True when the signature marked it <c>valuetype</c>.</param>
    /// <returns>The signature.</returns>
    public static IlSignature Unresolved(string name, bool isValueType) => new(IlSignatureKind.Named) { UnresolvedName = name, IsValueType = isValueType };

    /// <summary>
    /// A generic instance.
    /// </summary>
    /// <param name="definition">The generic type.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The signature.</returns>
    public static IlSignature GenericInstance(IlSignature definition, IReadOnlyList<IlSignature> arguments) => new(IlSignatureKind.GenericInstance) { Element = definition, Arguments = arguments, IsValueType = definition.IsValueType, Resolved = definition.Resolved };

    /// <summary>
    /// A vector.
    /// </summary>
    /// <param name="element">The element type.</param>
    /// <returns>The signature.</returns>
    public static IlSignature SzArray(IlSignature element) => new(IlSignatureKind.SzArray) { Element = element };

    /// <summary>
    /// A general array.
    /// </summary>
    /// <param name="element">The element type.</param>
    /// <param name="rank">The rank.</param>
    /// <param name="sizes">The declared sizes.</param>
    /// <param name="lowerBounds">The declared lower bounds.</param>
    /// <returns>The signature.</returns>
    public static IlSignature Array(IlSignature element, int rank, IReadOnlyList<int> sizes, IReadOnlyList<int> lowerBounds) => new(IlSignatureKind.Array) { Element = element, Rank = rank, Sizes = sizes, LowerBounds = lowerBounds };

    /// <summary>
    /// A managed pointer.
    /// </summary>
    /// <param name="element">The pointee.</param>
    /// <returns>The signature.</returns>
    public static IlSignature ByRef(IlSignature element) => new(IlSignatureKind.ByRef) { Element = element };

    /// <summary>
    /// An unmanaged pointer.
    /// </summary>
    /// <param name="element">The pointee.</param>
    /// <returns>The signature.</returns>
    public static IlSignature Pointer(IlSignature element) => new(IlSignatureKind.Pointer) { Element = element };

    /// <summary>
    /// A function pointer.
    /// </summary>
    /// <param name="method">The signature it points at.</param>
    /// <returns>The signature.</returns>
    public static IlSignature FunctionPointer(IlMethodSignature method) => new(IlSignatureKind.FunctionPointer) { Method = method };

    /// <summary>
    /// A modified type.
    /// </summary>
    /// <param name="element">The type modified.</param>
    /// <param name="modifier">The modifier type.</param>
    /// <param name="required">True for <c>modreq</c>.</param>
    /// <returns>The signature.</returns>
    public static IlSignature Modified(IlSignature element, IlSignature modifier, bool required) => new(IlSignatureKind.Modified) { Element = element, Modifier = modifier, IsRequired = required, IsValueType = element.IsValueType };

    /// <summary>
    /// A pinned local type.
    /// </summary>
    /// <param name="element">The type pinned.</param>
    /// <returns>The signature.</returns>
    public static IlSignature Pinned(IlSignature element) => new(IlSignatureKind.Pinned) { Element = element };

    /// <summary>
    /// A generic parameter of the declaring type.
    /// </summary>
    /// <param name="index">The position.</param>
    /// <param name="parameter">The runtime generic parameter when the context has one.</param>
    /// <returns>The signature.</returns>
    public static IlSignature TypeParameter(int index, Type? parameter) => new(IlSignatureKind.TypeParameter) { Index = index, Resolved = parameter };

    /// <summary>
    /// A generic parameter of the method.
    /// </summary>
    /// <param name="index">The position.</param>
    /// <param name="parameter">The runtime generic parameter when the context has one.</param>
    /// <returns>The signature.</returns>
    public static IlSignature MethodParameter(int index, Type? parameter) => new(IlSignatureKind.MethodParameter) { Index = index, Resolved = parameter };

    /// <summary>
    /// The signature without its custom modifiers and pinning.
    /// </summary>
    public IlSignature Unmodified => Kind is IlSignatureKind.Modified or IlSignatureKind.Pinned ? Element!.Unmodified : this;

    /// <summary>
    /// True when this or any part of it failed to resolve.
    /// </summary>
    public bool HasUnresolved => Kind switch
    {
        IlSignatureKind.Named => Resolved is null,
        IlSignatureKind.GenericInstance => Element!.HasUnresolved || Arguments.Any(a => a.HasUnresolved),
        IlSignatureKind.FunctionPointer => Method!.ReturnType.HasUnresolved || Method.Parameters.Any(p => p.HasUnresolved),
        IlSignatureKind.TypeParameter or IlSignatureKind.MethodParameter => Resolved is null,
        IlSignatureKind.Modified => Element!.HasUnresolved || Modifier!.HasUnresolved,
        IlSignatureKind.Primitive or IlSignatureKind.Sentinel => false,
        _ => Element!.HasUnresolved,
    };

    /// <summary>
    /// The closest runtime type, for the stack simulator: modifiers and pinning dropped, a function
    /// pointer as <c>native int</c> (its stack category), an unresolved part as null (an unknown slot).
    /// </summary>
    /// <returns>The type, or null when any part is unresolved.</returns>
    public Type? ToClrType()
    {
        switch (Kind)
        {
            case IlSignatureKind.Primitive:
            case IlSignatureKind.Named:
            case IlSignatureKind.TypeParameter:
            case IlSignatureKind.MethodParameter:
                return Resolved;
            case IlSignatureKind.GenericInstance:
            {
                var definition = Element!.ToClrType();
                var arguments = Arguments.Select(a => a.ToClrType()).ToArray();
                if (definition is null || !definition.IsGenericTypeDefinition || arguments.Any(a => a is null))
                {
                    return null;
                }

                try
                {
                    return definition.MakeGenericType(arguments!);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            case IlSignatureKind.SzArray:
                return Element!.ToClrType()?.MakeArrayType();
            case IlSignatureKind.Array:
                return Element!.ToClrType()?.MakeArrayType(Rank);
            case IlSignatureKind.ByRef:
                return Element!.ToClrType()?.MakeByRefType();
            case IlSignatureKind.Pointer:
                return Element!.ToClrType()?.MakePointerType();
            case IlSignatureKind.FunctionPointer:
                return typeof(nint);
            case IlSignatureKind.Modified:
            case IlSignatureKind.Pinned:
                return Element!.ToClrType();
            default:
                return null;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => IlSignatureRenderer.IlAsm(this);

    /// <summary>
    /// Builds a signature from a runtime type, for the paths that have no metadata reader: a
    /// dynamic assembly, or a reflected member's modifiers.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="requiredModifiers">Required modifiers to wrap it in, innermost first.</param>
    /// <param name="optionalModifiers">Optional modifiers to wrap it in, innermost first.</param>
    /// <returns>The signature.</returns>
    public static IlSignature FromType(Type type, IReadOnlyList<Type>? requiredModifiers = null, IReadOnlyList<Type>? optionalModifiers = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        var result = FromType(type);
        foreach (var m in requiredModifiers ?? [])
        {
            result = Modified(result, FromType(m), required: true);
        }

        foreach (var m in optionalModifiers ?? [])
        {
            result = Modified(result, FromType(m), required: false);
        }

        return result;
    }

    private static IlSignature FromType(Type type)
    {
        var keyword = TypeParser.PrimitiveKeyword(type);
        if (keyword is not null)
        {
            return Primitive(type, keyword);
        }

        if (type.IsGenericParameter)
        {
            return type.DeclaringMethod is null ? TypeParameter(type.GenericParameterPosition, type) : MethodParameter(type.GenericParameterPosition, type);
        }

        if (type.IsByRef)
        {
            return ByRef(FromType(type.GetElementType()!));
        }

        if (type.IsPointer)
        {
            return Pointer(FromType(type.GetElementType()!));
        }

        if (TypeNameFormatter.IsFunctionPointer(type))
        {
            return FunctionPointer(IlMethodSignature.FromFunctionPointer(type));
        }

        if (type.IsSZArray)
        {
            return SzArray(FromType(type.GetElementType()!));
        }

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            return Array(FromType(type.GetElementType()!), rank, [], Enumerable.Repeat(0, rank).ToArray());
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            return GenericInstance(Named(type.GetGenericTypeDefinition()), type.GetGenericArguments().Select(FromType).ToArray());
        }

        return Named(type);
    }
}
