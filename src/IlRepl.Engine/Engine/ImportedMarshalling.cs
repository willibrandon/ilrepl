using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Mono.Cecil;
using FieldDefinition = Mono.Cecil.FieldDefinition;

namespace IlRepl.Engine;

/// <summary>
/// Copies native marshalling descriptors without replacing omitted fields with reflection's default values.
/// </summary>
internal static class ImportedMarshalling
{
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
        var metadata = ModuleMetadata.TryOpen(module)
            ?? throw new ReplException($"marshalling metadata for token 0x{token:x8} is unavailable");
        var handle = MetadataTokens.EntityHandle(token);
        var descriptor = handle.Kind == HandleKind.FieldDefinition
            ? metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetMarshallingDescriptor()
            : metadata.GetParameter((ParameterHandle)handle).GetMarshallingDescriptor();
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
                result = new SafeArrayMarshalInfo
                {
                    ElementType = reader.RemainingBytes == 0 ? VariantType.None : (VariantType)reader.ReadByte(),
                };
                break;
            case NativeType.CustomMarshaler:
            {
                var guid = reader.ReadSerializedString();
                var unmanaged = reader.ReadSerializedString();
                var managed = reader.ReadSerializedString();
                var cookie = reader.ReadSerializedString();
                var type = Type.GetType(managed!, name => resolver.Assemblies.FirstOrDefault(assembly =>
                    string.Equals(assembly.FullName, name.FullName, StringComparison.OrdinalIgnoreCase)) ?? Assembly.Load(name),
                    null, throwOnError: true)!;
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
}
