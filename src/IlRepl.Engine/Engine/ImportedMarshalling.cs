using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Mono.Cecil;
using FieldDefinition = Mono.Cecil.FieldDefinition;
using ParameterAttributes = System.Reflection.ParameterAttributes;

namespace IlRepl.Engine;

/// <summary>
/// Copies native marshalling descriptors without replacing omitted fields with reflection's default values.
/// </summary>
internal static class ImportedMarshalling
{
    /// <summary>
    /// Finds a parameter's metadata row by sequence, including a separately declared return parameter.
    /// </summary>
    /// <param name="parameter">The runtime parameter.</param>
    /// <returns>The exact Param table token.</returns>
    internal static int ParameterToken(ParameterInfo parameter)
    {
        var method = parameter.Member;
        var metadata = ModuleMetadata.TryOpen(method.Module)
            ?? throw new ReplException($"parameter metadata for {method.Name} is unavailable");
        var definition = metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff));
        return MetadataTokens.GetToken(definition.GetParameters().FirstOrDefault(handle =>
            metadata.GetParameter(handle).SequenceNumber == parameter.Position + 1));
    }

    /// <summary>
    /// Reads parameter flags without the runtime's synthetic return-parameter projection.
    /// </summary>
    /// <param name="parameter">The runtime parameter.</param>
    /// <returns>The declared flags, or no flags when the parameter has no metadata row.</returns>
    internal static ParameterAttributes Attributes(ParameterInfo parameter)
    {
        var token = ParameterToken(parameter);
        return (token & 0x00ffffff) == 0 ? ParameterAttributes.None : ModuleMetadata.TryOpen(parameter.Member.Module)!
            .GetParameter(MetadataTokens.ParameterHandle(token & 0x00ffffff)).Attributes;
    }

    /// <summary>
    /// Resolves a custom marshaler or SAFEARRAY subtype before the copied family assigns its destination types.
    /// </summary>
    /// <param name="module">The source module.</param>
    /// <param name="token">The field or parameter metadata token.</param>
    /// <param name="resolver">The session's loaded assemblies.</param>
    /// <returns>The referenced type and whether it requires a custom marshaler factory.</returns>
    internal static (Type? Type, bool CustomMarshaler) Dependency(Module module, int token, TypeResolver resolver)
    {
        var (metadata, descriptor) = Descriptor(module, token);
        if (descriptor.IsNil)
        {
            return (null, false);
        }

        var reader = metadata.GetBlobReader(descriptor);
        var native = (NativeType)reader.ReadByte();
        if (native == NativeType.CustomMarshaler)
        {
            reader.ReadSerializedString();
            reader.ReadSerializedString();
            return (ResolveType(reader.ReadSerializedString()!, module, resolver), true);
        }

        if (native == NativeType.SafeArray && reader.RemainingBytes != 0)
        {
            reader.ReadCompressedInteger();
            if (reader.RemainingBytes != 0 && reader.ReadSerializedString() is { Length: > 0 } subtype)
            {
                return (ResolveType(subtype, module, resolver), false);
            }
        }

        return (null, false);
    }

    internal static MarshalInfo Read(Module module, int token, CecilWriter writer, TypeResolver resolver, IMarshalInfoProvider target)
    {
        try
        {
            return ReadCore(module, token, writer, resolver, target);
        }
        catch (BadImageFormatException exception)
        {
            var name = target switch
            {
                FieldDefinition field => "field " + field.Name,
                ParameterDefinition parameter => "parameter " + parameter.Name,
                _ => "return parameter",
            };
            throw new ReplException($"invalid marshalling descriptor for {name} at token 0x{token:x8}: {exception.Message}", exception);
        }
    }

    private static MarshalInfo ReadCore(Module module, int token, CecilWriter writer, TypeResolver resolver, IMarshalInfoProvider target)
    {
        var (metadata, descriptor) = Descriptor(module, token);
        var reader = metadata.GetBlobReader(descriptor);
        var native = (NativeType)reader.ReadByte();
        MarshalInfo result;
        switch (native)
        {
            case NativeType.Array:
                result = new ArrayMarshalInfo
                {
                    ElementType = reader.RemainingBytes == 0 ? NativeType.None : (NativeType)reader.ReadByte(),
                    SizeParameterIndex = OptionalInteger(ref reader),
                    Size = OptionalInteger(ref reader),
                    SizeParameterMultiplier = OptionalInteger(ref reader),
                };
                break;
            case NativeType.FixedArray:
                result = new FixedArrayMarshalInfo
                {
                    Size = reader.ReadCompressedInteger(),
                    ElementType = reader.RemainingBytes == 0 ? NativeType.None : (NativeType)reader.ReadByte(),
                };
                break;
            case NativeType.FixedSysString:
                result = new FixedSysStringMarshalInfo { Size = reader.ReadCompressedInteger() };
                break;
            case NativeType.SafeArray:
            {
                result = new SafeArrayMarshalInfo
                {
                    ElementType = reader.RemainingBytes == 0 ? VariantType.None : (VariantType)reader.ReadCompressedInteger(),
                };
                if (reader.RemainingBytes != 0)
                {
                    var prefix = reader.Offset;
                    var subtype = reader.ReadSerializedString();
                    var bytes = metadata.GetBlobBytes(descriptor);
                    if (!string.IsNullOrEmpty(subtype))
                    {
                        var imported = writer.Import(ResolveType(subtype, module, resolver));
                        if (CecilSerializedTypeName.References(imported, writer.Module))
                        {
                            var remapped = new BlobBuilder();
                            remapped.WriteBytes(bytes, 0, prefix);
                            remapped.WriteSerializedString(CecilSerializedTypeName.Format(imported));
                            remapped.WriteBytes(reader.ReadBytes(reader.RemainingBytes));
                            bytes = remapped.ToArray();
                        }
                    }

                    return writer.SignatureFixups.Marshal(target, bytes, writer.Object);
                }

                break;
            }
            case NativeType.CustomMarshaler:
            {
                var guid = reader.ReadSerializedString();
                var unmanaged = reader.ReadSerializedString();
                var managed = reader.ReadSerializedString();
                var cookie = reader.ReadSerializedString();
                var type = ResolveType(managed!, module, resolver);
                result = new CustomMarshalInfo
                {
                    Guid = string.IsNullOrEmpty(guid) ? Guid.Empty : Guid.Parse(guid),
                    UnmanagedType = unmanaged,
                    ManagedType = writer.Import(type),
                    Cookie = cookie,
                };
                break;
            }
            default:
                result = new MarshalInfo(native);
                break;
        }

        if (reader.RemainingBytes != 0)
        {
            return writer.SignatureFixups.Marshal(target, metadata.GetBlobBytes(descriptor), writer.Object);
        }

        return result;
    }

    private static int OptionalInteger(ref BlobReader reader) => reader.RemainingBytes == 0 ? -1 : reader.ReadCompressedInteger();

    private static (MetadataReader Metadata, BlobHandle Descriptor) Descriptor(Module module, int token)
    {
        var metadata = ModuleMetadata.TryOpen(module)
            ?? throw new ReplException($"marshalling metadata for token 0x{token:x8} is unavailable");
        var handle = MetadataTokens.EntityHandle(token);
        var descriptor = handle.Kind == HandleKind.FieldDefinition
            ? metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetMarshallingDescriptor()
            : metadata.GetParameter((ParameterHandle)handle).GetMarshallingDescriptor();
        return (metadata, descriptor);
    }

    private static Type ResolveType(string name, Module module, TypeResolver resolver) => Type.GetType(name,
        identity => resolver.Assemblies.Prepend(module.Assembly).FirstOrDefault(assembly =>
            AssemblyName.ReferenceMatchesDefinition(identity, assembly.GetName())
            && (identity.Version is null || identity.Version == assembly.GetName().Version)
            && (identity.GetPublicKeyToken() is not { Length: > 0 } token
                || token.AsSpan().SequenceEqual(assembly.GetName().GetPublicKeyToken())))
            ?? SessionAssemblies.Resolve(identity) ?? Assembly.Load(identity),
        (assembly, type, ignoreCase) => (assembly ?? module.Assembly).GetType(type, throwOnError: true, ignoreCase), throwOnError: true)!;
}
