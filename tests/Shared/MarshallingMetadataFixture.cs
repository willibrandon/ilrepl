using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Creates a managed method whose only reference to a custom marshaler is a native descriptor.
/// </summary>
public static class MarshallingMetadataFixture
{
    /// <summary>
    /// Writes an executable image with a custom marshal descriptor on the requested metadata target.
    /// </summary>
    /// <param name="marshaler">The assembly-qualified custom marshaler type name.</param>
    /// <param name="target">The field, parameter, or return target.</param>
    /// <param name="assemblyName">The registered session assembly name, or null for a separate fixture assembly.</param>
    /// <returns>The complete PE image.</returns>
    public static byte[] Create(string marshaler, string target, string? assemblyName = null)
    {
        var metadata = new MetadataBuilder();
        var name = assemblyName ?? "MarshallingFixture" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = metadata.AddTypeReference(reference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var field = metadata.AddFieldDefinition(FieldAttributes.Public
            | (target == "field" ? FieldAttributes.HasFieldMarshal : 0), metadata.GetOrAddString("Value"),
            metadata.GetOrAddBlob((byte[])[6, 0x1c]));
        var result = metadata.AddParameter(target == "return" ? ParameterAttributes.HasFieldMarshal : 0, default, 0);
        var parameter = metadata.AddParameter(target == "parameter" ? ParameterAttributes.HasFieldMarshal : 0,
            metadata.GetOrAddString("value"), 1);
        var descriptor = new BlobBuilder();
        descriptor.WriteByte(0x2c);
        descriptor.WriteSerializedString("");
        descriptor.WriteSerializedString("");
        descriptor.WriteSerializedString(marshaler);
        descriptor.WriteSerializedString("saved cookie");
        metadata.AddMarshallingDescriptor(target == "field" ? field : target == "return" ? result : parameter,
            metadata.GetOrAddBlob(descriptor));
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        var il = new InstructionEncoder(new BlobBuilder());
        il.LoadArgument(0);
        il.OpCode(ILOpCode.Ret);
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob((byte[])[0, 1, 8, 8]), bodies.AddMethodBody(il), result);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();
    }
}
