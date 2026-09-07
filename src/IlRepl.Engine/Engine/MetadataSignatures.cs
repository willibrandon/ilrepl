using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace IlRepl.Engine;

/// <summary>
/// Reads the signatures a listing prints from a module's metadata: locals, member references,
/// method definitions, <c>calli</c> signatures, and type tokens.
/// </summary>
public static class MetadataSignatures
{
    /// <summary>
    /// Decodes a method definition's signature.
    /// </summary>
    /// <param name="reader">The metadata reader.</param>
    /// <param name="token">The MethodDef token.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="context">The generic context.</param>
    /// <returns>The signature.</returns>
    public static IlMethodSignature MethodDefinition(MetadataReader reader, int token, MetadataSignatureProvider provider, GenericContext context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(token);
        return Convert(reader.GetMethodDefinition(handle).DecodeSignature(provider, context));
    }

    /// <summary>
    /// Decodes the signature behind a method operand: a MethodDef, a MemberRef, or a MethodSpec
    /// (whose signature is the underlying method's, with the instantiation returned separately).
    /// </summary>
    /// <param name="reader">The metadata reader.</param>
    /// <param name="token">The operand token.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="context">The generic context.</param>
    /// <param name="instantiation">The generic arguments of a MethodSpec, or null.</param>
    /// <returns>The signature, or null when the token is not a method.</returns>
    public static IlMethodSignature? MethodOperand(MetadataReader reader, int token, MetadataSignatureProvider provider, GenericContext context, out IReadOnlyList<IlSignature>? instantiation)
    {
        ArgumentNullException.ThrowIfNull(reader);
        instantiation = null;
        var handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                return Convert(reader.GetMethodDefinition((MethodDefinitionHandle)handle).DecodeSignature(provider, context));
            case HandleKind.MemberReference:
            {
                var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                return reference.GetKind() == MemberReferenceKind.Method ? Convert(reference.DecodeMethodSignature(provider, context)) : null;
            }

            case HandleKind.MethodSpecification:
            {
                var spec = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                instantiation = spec.DecodeSignature(provider, context);
                return MethodOperand(reader, MetadataTokens.GetToken(spec.Method), provider, context, out _);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Decodes the signature behind a field operand: a FieldDef or a MemberRef.
    /// </summary>
    /// <param name="reader">The metadata reader.</param>
    /// <param name="token">The operand token.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="context">The generic context.</param>
    /// <returns>The field type, or null when the token is not a field.</returns>
    public static IlSignature? FieldOperand(MetadataReader reader, int token, MetadataSignatureProvider provider, GenericContext context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.FieldDefinition:
                return reader.GetFieldDefinition((FieldDefinitionHandle)handle).DecodeSignature(provider, context);
            case HandleKind.MemberReference:
            {
                var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                return reference.GetKind() == MemberReferenceKind.Field ? reference.DecodeFieldSignature(provider, context) : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Decodes a type operand: a TypeDef, TypeRef, or TypeSpec token.
    /// </summary>
    /// <param name="reader">The metadata reader.</param>
    /// <param name="token">The token.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="context">The generic context.</param>
    /// <returns>The signature, or null when the token is not a type.</returns>
    public static IlSignature? TypeOperand(MetadataReader reader, int token, MetadataSignatureProvider provider, GenericContext context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(provider);
        var handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => provider.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => provider.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => provider.GetTypeFromSpecification(reader, context, (TypeSpecificationHandle)handle, 0),
            _ => null,
        };
    }

    /// <summary>
    /// Decodes a <c>calli</c> operand: a StandAloneSig token holding a method signature.
    /// </summary>
    /// <param name="reader">The metadata reader.</param>
    /// <param name="token">The token.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="context">The generic context.</param>
    /// <returns>The signature, or null when the token is not a method signature.</returns>
    public static IlMethodSignature? StandaloneMethod(MetadataReader reader, int token, MetadataSignatureProvider provider, GenericContext context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.StandaloneSignature)
        {
            return null;
        }

        var signature = reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
        return signature.GetKind() == StandaloneSignatureKind.Method ? Convert(signature.DecodeMethodSignature(provider, context)) : null;
    }

    /// <summary>
    /// Decodes a method body's local signature.
    /// </summary>
    /// <param name="reader">The metadata reader.</param>
    /// <param name="token">The StandAloneSig token from the body header, or 0 for no locals.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="context">The generic context.</param>
    /// <returns>The local types in slot order, or null when the token is not a local signature.</returns>
    public static IReadOnlyList<IlSignature>? Locals(MetadataReader reader, int token, MetadataSignatureProvider provider, GenericContext context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (token == 0)
        {
            return [];
        }

        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind != HandleKind.StandaloneSignature)
        {
            return null;
        }

        var signature = reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
        return signature.GetKind() == StandaloneSignatureKind.LocalVariables ? signature.DecodeLocalSignature(provider, context) : null;
    }

    /// <summary>
    /// Converts the decoder's method signature into the engine's.
    /// </summary>
    /// <param name="signature">The decoded signature.</param>
    /// <returns>The engine signature.</returns>
    public static IlMethodSignature Convert(MethodSignature<IlSignature> signature)
    {
        var header = signature.Header;
        return new IlMethodSignature(
            header.CallingConvention,
            header.IsInstance,
            header.Attributes.HasFlag(SignatureAttributes.ExplicitThis),
            signature.GenericParameterCount,
            signature.ReturnType,
            signature.ParameterTypes,
            signature.RequiredParameterCount);
    }
}
