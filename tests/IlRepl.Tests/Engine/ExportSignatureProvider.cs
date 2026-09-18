using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Decodes exported signatures without relying on Cecil or ilrepl's metadata readers.
/// </summary>
internal sealed class ExportSignatureProvider : ISignatureTypeProvider<string, object?>
{
    /// <summary>
    /// Describes a multidimensional array including explicitly declared sizes and lower bounds.
    /// </summary>
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + shape.Rank + ";"
        + string.Join(',', shape.Sizes) + ";" + string.Join(',', shape.LowerBounds) + "]";

    /// <summary>
    /// Describes a managed pointer.
    /// </summary>
    public string GetByReferenceType(string elementType) => elementType + "&";

    /// <summary>
    /// Preserves a function pointer's complete calling signature.
    /// </summary>
    public string GetFunctionPointerType(MethodSignature<string> signature) => "method " + Method(signature);

    /// <summary>
    /// Preserves a closed generic type and the order of its arguments.
    /// </summary>
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => genericType + "<" + string.Join(',', typeArguments) + ">";

    /// <summary>
    /// Distinguishes method generic parameters from owner generic parameters.
    /// </summary>
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

    /// <summary>
    /// Preserves an owner generic parameter's index.
    /// </summary>
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

    /// <summary>
    /// Preserves required and optional custom modifiers in their original order.
    /// </summary>
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        => unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";

    /// <summary>
    /// Preserves pinned local signatures.
    /// </summary>
    public string GetPinnedType(string elementType) => elementType + " pinned";

    /// <summary>
    /// Describes an unmanaged pointer.
    /// </summary>
    public string GetPointerType(string elementType) => elementType + "*";

    /// <summary>
    /// Preserves the primitive element kind.
    /// </summary>
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

    /// <summary>
    /// Distinguishes a vector from a rank-one multidimensional array.
    /// </summary>
    public string GetSZArrayType(string elementType) => elementType + "[]";

    /// <summary>
    /// Resolves an authored definition name, including its declaring types.
    /// </summary>
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var definition = reader.GetTypeDefinition(handle);
        return definition.IsNested
            ? GetTypeFromDefinition(reader, definition.GetDeclaringType(), rawTypeKind) + "/" + reader.GetString(definition.Name)
            : Name(reader.GetString(definition.Namespace), reader.GetString(definition.Name));
    }

    /// <summary>
    /// Resolves external and nested reference names with their binding scopes independently of metadata token allocation.
    /// </summary>
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var reference = reader.GetTypeReference(handle);
        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? GetTypeFromReference(reader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind)
                + "/" + reader.GetString(reference.Name)
            : Scope(reader, reference.ResolutionScope) + Name(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
    }

    /// <summary>
    /// Decodes a constructed signature using the same independent reader.
    /// </summary>
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    /// <summary>
    /// Resolves any type-bearing metadata handle to its semantic signature.
    /// </summary>
    internal string Type(MetadataReader reader, EntityHandle handle) => handle.IsNil ? "" : handle.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
        _ => throw new BadImageFormatException("Unexpected type handle: " + handle.Kind),
    };

    /// <summary>
    /// Describes a method's convention, generic arity, sentinel position, and complete signature.
    /// </summary>
    internal static string Method(MethodSignature<string> signature) => signature.Header.RawValue + ":"
        + signature.GenericParameterCount + ":" + signature.RequiredParameterCount + ":" + signature.ReturnType
        + "(" + string.Join(',', signature.ParameterTypes) + ")";

    private static string Name(string space, string name) => space.Length == 0 ? name : space + "." + name;

    private static string Scope(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.AssemblyReference => AssemblyScope(reader, (AssemblyReferenceHandle)handle),
        HandleKind.ModuleReference => "[module " + reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)handle).Name) + "]",
        HandleKind.ModuleDefinition => "[module " + reader.GetString(reader.GetModuleDefinition().Name) + "]",
        _ => throw new BadImageFormatException("Unexpected reference scope: " + handle.Kind),
    };

    private static string AssemblyScope(MetadataReader reader, AssemblyReferenceHandle handle)
    {
        var reference = reader.GetAssemblyReference(handle);
        return "[" + reader.GetString(reference.Name) + ", " + reference.Version + ", " + reader.GetString(reference.Culture)
            + ", " + Convert.ToHexString(reader.GetBlobBytes(reference.PublicKeyOrToken)) + ", " + reference.Flags + "]";
    }
}
