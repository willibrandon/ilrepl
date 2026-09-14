using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds an array method whose private subtype is referenced only by a SAFEARRAY descriptor.
/// </summary>
public static class SafeArrayMetadataFixture
{
    /// <summary>
    /// Writes an executable array identity method and a private subtype with an escaped metadata name.
    /// </summary>
    /// <param name="target">The field, parameter, or return descriptor.</param>
    /// <param name="generic">Whether the subtype is a generic collection containing an array of the private type.</param>
    /// <returns>The complete PE image.</returns>
    public static byte[] Create(string target, bool generic)
    {
        var metadata = new MetadataBuilder();
        var name = "SafeArray" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = metadata.AddTypeReference(reference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var hidden = metadata.AddTypeDefinition(TypeAttributes.NestedPrivate, default, metadata.GetOrAddString("Hidden+Record"), objectType,
            MetadataTokens.FieldDefinitionHandle(2), MetadataTokens.MethodDefinitionHandle(2));
        metadata.AddNestedType(hidden, owner);
        var field = metadata.AddFieldDefinition(FieldAttributes.Public | (target == "field" ? FieldAttributes.HasFieldMarshal : 0),
            metadata.GetOrAddString("Items"), metadata.GetOrAddBlob((byte[])[6, 0x1d, 0x1c]));
        metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("Value"), metadata.GetOrAddBlob((byte[])[6, 8]));
        var result = metadata.AddParameter(target == "return" ? ParameterAttributes.HasFieldMarshal : 0, default, 0);
        var parameter = metadata.AddParameter(target == "parameter" ? ParameterAttributes.HasFieldMarshal : 0,
            metadata.GetOrAddString("items"), 1);
        var subtype = "Owner+Hidden\\+Record" + (generic ? "[]" : "") + ", " + name;
        if (generic)
        {
            subtype = "System.Collections.Generic.List`1[[" + subtype + "]], " + core.FullName;
        }

        var descriptor = new BlobBuilder();
        descriptor.WriteByte(0x1d);
        descriptor.WriteCompressedInteger(13);
        descriptor.WriteSerializedString(subtype);
        metadata.AddMarshallingDescriptor(target == "field" ? field : target == "return" ? result : parameter,
            metadata.GetOrAddBlob(descriptor));
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        var il = new InstructionEncoder(new BlobBuilder());
        il.LoadArgument(0);
        il.OpCode(ILOpCode.Ret);
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob((byte[])[0, 1, 0x1d, 0x1c, 0x1d, 0x1c]),
            bodies.AddMethodBody(il), result);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();
    }
}
