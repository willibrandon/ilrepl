using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace IlRepl.Engine;

/// <summary>
/// Turns signature blobs into <see cref="IlSignature"/> trees for <see cref="SignatureDecoder{TType, TGenericContext}"/>.
/// Named types resolve through a callback (the module's <c>ResolveType</c> wrapped in the caller's
/// failure handling); a type that does not resolve keeps its spelling from the metadata row.
/// </summary>
/// <param name="resolve">Resolves a metadata token to a runtime type, or returns null.</param>
public sealed class MetadataSignatureProvider(Func<int, Type?> resolve) : ISignatureTypeProvider<IlSignature, GenericContext>
{
    /// <inheritdoc/>
    public IlSignature GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        var type = typeCode switch
        {
            PrimitiveTypeCode.Boolean => typeof(bool),
            PrimitiveTypeCode.Char => typeof(char),
            PrimitiveTypeCode.SByte => typeof(sbyte),
            PrimitiveTypeCode.Byte => typeof(byte),
            PrimitiveTypeCode.Int16 => typeof(short),
            PrimitiveTypeCode.UInt16 => typeof(ushort),
            PrimitiveTypeCode.Int32 => typeof(int),
            PrimitiveTypeCode.UInt32 => typeof(uint),
            PrimitiveTypeCode.Int64 => typeof(long),
            PrimitiveTypeCode.UInt64 => typeof(ulong),
            PrimitiveTypeCode.Single => typeof(float),
            PrimitiveTypeCode.Double => typeof(double),
            PrimitiveTypeCode.IntPtr => typeof(nint),
            PrimitiveTypeCode.UIntPtr => typeof(nuint),
            PrimitiveTypeCode.Object => typeof(object),
            PrimitiveTypeCode.String => typeof(string),
            PrimitiveTypeCode.Void => typeof(void),
            PrimitiveTypeCode.TypedReference => typeof(TypedReference),
            _ => throw new BadImageFormatException($"unexpected primitive type code {typeCode}"),
        };
        return IlSignature.Primitive(type, TypeParser.PrimitiveKeyword(type)!);
    }

    /// <inheritdoc/>
    public IlSignature GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var resolved = resolve(MetadataTokens.GetToken(handle));
        if (resolved is not null)
        {
            return IlSignature.Named(resolved);
        }

        var definition = reader.GetTypeDefinition(handle);
        var name = DefinitionName(reader, definition);
        var assembly = reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : reader.GetString(reader.GetModuleDefinition().Name);
        return IlSignature.Unresolved($"[{assembly}]{name}", rawTypeKind == (byte)SignatureTypeKind.ValueType);
    }

    /// <inheritdoc/>
    public IlSignature GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var resolved = resolve(MetadataTokens.GetToken(handle));
        return resolved is not null
            ? IlSignature.Named(resolved)
            : IlSignature.Unresolved(ReferenceName(reader, reader.GetTypeReference(handle)), rawTypeKind == (byte)SignatureTypeKind.ValueType);
    }

    /// <inheritdoc/>
    public IlSignature GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    /// <inheritdoc/>
    public IlSignature GetSZArrayType(IlSignature elementType) => IlSignature.SzArray(elementType);

    /// <inheritdoc/>
    public IlSignature GetArrayType(IlSignature elementType, ArrayShape shape) => IlSignature.Array(elementType, shape.Rank, shape.Sizes, shape.LowerBounds);

    /// <inheritdoc/>
    public IlSignature GetByReferenceType(IlSignature elementType) => IlSignature.ByRef(elementType);

    /// <inheritdoc/>
    public IlSignature GetPointerType(IlSignature elementType) => IlSignature.Pointer(elementType);

    /// <inheritdoc/>
    public IlSignature GetGenericInstantiation(IlSignature genericType, ImmutableArray<IlSignature> typeArguments) => IlSignature.GenericInstance(genericType, typeArguments);

    /// <inheritdoc/>
    public IlSignature GetFunctionPointerType(MethodSignature<IlSignature> signature) => IlSignature.FunctionPointer(MetadataSignatures.Convert(signature));

    /// <inheritdoc/>
    public IlSignature GetGenericMethodParameter(GenericContext genericContext, int index)
    {
        ArgumentNullException.ThrowIfNull(genericContext);
        return IlSignature.MethodParameter(index, index < genericContext.MethodArguments.Count ? genericContext.MethodArguments[index] : null);
    }

    /// <inheritdoc/>
    public IlSignature GetGenericTypeParameter(GenericContext genericContext, int index)
    {
        ArgumentNullException.ThrowIfNull(genericContext);
        return IlSignature.TypeParameter(index, index < genericContext.TypeArguments.Count ? genericContext.TypeArguments[index] : null);
    }

    /// <inheritdoc/>
    public IlSignature GetModifiedType(IlSignature modifier, IlSignature unmodifiedType, bool isRequired) => IlSignature.Modified(unmodifiedType, modifier, isRequired);

    /// <inheritdoc/>
    public IlSignature GetPinnedType(IlSignature elementType) => IlSignature.Pinned(elementType);

    private static string DefinitionName(MetadataReader reader, TypeDefinition definition)
    {
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return DefinitionName(reader, reader.GetTypeDefinition(declaring)) + "/" + name;
        }

        var ns = reader.GetString(definition.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string ReferenceName(MetadataReader reader, TypeReference reference)
    {
        var name = reader.GetString(reference.Name);
        var ns = reader.GetString(reference.Namespace);
        var full = ns.Length == 0 ? name : ns + "." + name;
        var scope = reference.ResolutionScope;
        switch (scope.Kind)
        {
            case HandleKind.TypeReference:
                return ReferenceName(reader, reader.GetTypeReference((TypeReferenceHandle)scope)) + "/" + name;
            case HandleKind.AssemblyReference:
                return $"[{reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)}]{full}";
            case HandleKind.ModuleReference:
                return $"[.module {reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)scope).Name)}]{full}";
            default:
                return full;
        }
    }
}
