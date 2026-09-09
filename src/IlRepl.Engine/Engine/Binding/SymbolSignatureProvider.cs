using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Decodes metadata signatures into symbols while retaining unresolved external references.
/// </summary>
/// <remarks>
/// Turns signature blobs into symbols for the metadata signature decoder.
/// A type definition of the module is its symbol; a type reference resolves through the catalog
/// to the definition it names in another loaded assembly, or stays an unresolved spelling.
/// </remarks>
public sealed class SymbolSignatureProvider : ISignatureTypeProvider<TypeSymbol, SymbolGenericOwner>
{
    private readonly AssemblySymbolSource _source;
    private readonly LoadedBindingCatalog _catalog;
    private readonly Func<TypeReferenceHandle, byte, TypeSymbol>? _reference;

    /// <summary>
    /// Initializes a provider over a module and the catalog its references resolve through.
    /// </summary>
    /// <param name="source">The module's symbol source.</param>
    /// <param name="catalog">The catalog.</param>
    public SymbolSignatureProvider(AssemblySymbolSource source, LoadedBindingCatalog catalog)
        : this(source, catalog, null)
    {
    }

    internal SymbolSignatureProvider(AssemblySymbolSource source, LoadedBindingCatalog catalog,
        Func<TypeReferenceHandle, byte, TypeSymbol>? reference)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);
        _source = source;
        _catalog = catalog;
        _reference = reference;
    }

    /// <inheritdoc/>
    public TypeSymbol GetPrimitiveType(PrimitiveTypeCode typeCode) => TypeSymbol.Primitive(typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "int8",
        PrimitiveTypeCode.Byte => "uint8",
        PrimitiveTypeCode.Int16 => "int16",
        PrimitiveTypeCode.UInt16 => "uint16",
        PrimitiveTypeCode.Int32 => "int32",
        PrimitiveTypeCode.UInt32 => "uint32",
        PrimitiveTypeCode.Int64 => "int64",
        PrimitiveTypeCode.UInt64 => "uint64",
        PrimitiveTypeCode.Single => "float32",
        PrimitiveTypeCode.Double => "float64",
        PrimitiveTypeCode.IntPtr => "native int",
        PrimitiveTypeCode.UIntPtr => "native uint",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => throw new BadImageFormatException($"unexpected primitive type code {typeCode}"),
    });

    /// <inheritdoc/>
    public TypeSymbol GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => _source.Definition(
        handle);

    /// <inheritdoc/>
    public TypeSymbol GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (_reference is not null)
        {
            return _reference(handle, rawTypeKind);
        }

        return _catalog.ResolveTypeReference(_source, handle) ?? _source.UnresolvedReference(handle, rawTypeKind == (
            byte)SignatureTypeKind.ValueType);
    }

    /// <inheritdoc/>
    public TypeSymbol GetTypeFromSpecification(MetadataReader reader, SymbolGenericOwner genericContext, TypeSpecificationHandle handle,
        byte rawTypeKind)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    /// <inheritdoc/>
    public TypeSymbol GetSZArrayType(TypeSymbol elementType) => TypeSymbol.SzArray(elementType);

    /// <inheritdoc/>
    public TypeSymbol GetArrayType(TypeSymbol elementType, ArrayShape shape) => TypeSymbol.Array(elementType, shape.Rank, shape.Sizes,
        shape.LowerBounds);

    /// <inheritdoc/>
    public TypeSymbol GetByReferenceType(TypeSymbol elementType) => TypeSymbol.ByRef(elementType);

    /// <inheritdoc/>
    public TypeSymbol GetPointerType(TypeSymbol elementType) => TypeSymbol.Pointer(elementType);

    /// <inheritdoc/>
    public TypeSymbol GetGenericInstantiation(TypeSymbol genericType, ImmutableArray<TypeSymbol> typeArguments) =>
        genericType.Kind is TypeSymbolKind.Named or TypeSymbolKind.Unresolved
            ? TypeSymbol.Construct(genericType, typeArguments)
            : TypeSymbol.Unresolved(SymbolRenderer.Pretty(genericType), "", "", genericType.IsValueType);

    /// <inheritdoc/>
    public TypeSymbol GetFunctionPointerType(MethodSignature<TypeSymbol> signature) => TypeSymbol.FunctionPointer(Convert(signature));

    /// <inheritdoc/>
    public TypeSymbol GetGenericMethodParameter(SymbolGenericOwner genericContext, int index)
    {
        ArgumentNullException.ThrowIfNull(genericContext);
        return genericContext.MethodParameter(index);
    }

    /// <inheritdoc/>
    public TypeSymbol GetGenericTypeParameter(SymbolGenericOwner genericContext, int index)
    {
        ArgumentNullException.ThrowIfNull(genericContext);
        return genericContext.TypeParameter(index);
    }

    /// <inheritdoc/>
    public TypeSymbol GetModifiedType(TypeSymbol modifier, TypeSymbol unmodifiedType, bool isRequired) => TypeSymbol.Modified(
        unmodifiedType, modifier, isRequired);

    /// <inheritdoc/>
    public TypeSymbol GetPinnedType(TypeSymbol elementType) => TypeSymbol.Pinned(elementType);

    /// <summary>
    /// The engine's signature for a decoded standalone or function pointer signature.
    /// </summary>
    /// <param name="signature">The decoded signature.</param>
    /// <returns>The symbol.</returns>
    public static MethodSignatureSymbol Convert(MethodSignature<TypeSymbol> signature)
    {
        var header = signature.Header;
        var managed = header.CallingConvention == SignatureCallingConvention.VarArgs
            ? CallingConventions.VarArgs : CallingConventions.Standard;
        if (header.IsInstance)
        {
            managed |= CallingConventions.HasThis;
        }

        if (header.Attributes.HasFlag(SignatureAttributes.ExplicitThis))
        {
            managed |= CallingConventions.ExplicitThis;
        }

        var unmanaged = header.CallingConvention switch
        {
            SignatureCallingConvention.CDecl => System.Runtime.InteropServices.CallingConvention.Cdecl,
            SignatureCallingConvention.StdCall => System.Runtime.InteropServices.CallingConvention.StdCall,
            SignatureCallingConvention.ThisCall => System.Runtime.InteropServices.CallingConvention.ThisCall,
            SignatureCallingConvention.FastCall => System.Runtime.InteropServices.CallingConvention.FastCall,
            _ => System.Runtime.InteropServices.CallingConvention.Winapi,
        };
        var isUnmanaged = header.CallingConvention is SignatureCallingConvention.CDecl or SignatureCallingConvention.StdCall
            or SignatureCallingConvention.ThisCall or SignatureCallingConvention.FastCall or SignatureCallingConvention.Unmanaged;
        int? sentinel = signature.RequiredParameterCount < signature.ParameterTypes.Length ? signature.RequiredParameterCount : null;
        return new MethodSignatureSymbol(managed, isUnmanaged, unmanaged, StripModifiers(signature.ReturnType, out _, out _),
            [.. signature.ParameterTypes.Select(p => StripModifiers(p, out _, out _))], sentinel);
    }

    /// <summary>
    /// Separates a type from the custom modifiers carried beside its signature.
    /// </summary>
    /// <remarks>
    /// The type under its custom modifiers, with the modifiers set apart the way the runtime
    /// reports them beside a parameter.
    /// </remarks>
    /// <param name="type">The decoded type.</param>
    /// <param name="required">The <c>modreq</c> types, in order.</param>
    /// <param name="optional">The <c>modopt</c> types, in order.</param>
    /// <returns>The unmodified type.</returns>
    public static TypeSymbol StripModifiers(TypeSymbol type, out IReadOnlyList<TypeSymbol> required, out IReadOnlyList<TypeSymbol> optional)
    {
        ArgumentNullException.ThrowIfNull(type);
        var requiredList = new List<TypeSymbol>();
        var optionalList = new List<TypeSymbol>();
        var current = type;
        while (current.Kind is TypeSymbolKind.Modified or TypeSymbolKind.Pinned)
        {
            if (current.Kind == TypeSymbolKind.Modified)
            {
                (current.IsRequired ? requiredList : optionalList).Add(current.Modifier!);
            }

            current = current.Element!;
        }

        requiredList.Reverse();
        optionalList.Reverse();
        required = requiredList;
        optional = optionalList;
        return current;
    }
}
