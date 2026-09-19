using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds a real assembly whose selected method discovers a forwarded framework type.
/// </summary>
public static class ForwardedTypeFixture
{
    /// <summary>
    /// Emits a System.String forwarder and a method that returns the number of forwarded types.
    /// </summary>
    /// <returns>The complete portable executable image.</returns>
    public static byte[] Create()
    {
        var metadata = new MetadataBuilder();
        var name = "ForwardedTypes" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = metadata.AddTypeReference(reference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        var assemblyType = metadata.AddTypeReference(reference, metadata.GetOrAddString("System.Reflection"),
            metadata.GetOrAddString("Assembly"));
        var reflectedType = metadata.AddTypeReference(reference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Type"));
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddExportedType(TypeAttributes.Public | (TypeAttributes)0x00200000, metadata.GetOrAddString("System"),
            metadata.GetOrAddString("String"), reference, 0);
        var executingSignature = new BlobBuilder();
        new BlobEncoder(executingSignature).MethodSignature().Parameters(0,
            result => result.Type().Type(assemblyType, isValueType: false), _ =>
            {
            });
        var executing = metadata.AddMemberReference(assemblyType, metadata.GetOrAddString("GetExecutingAssembly"),
            metadata.GetOrAddBlob(executingSignature));
        var forwardedSignature = new BlobBuilder();
        new BlobEncoder(forwardedSignature).MethodSignature(isInstanceMethod: true).Parameters(0,
            result => result.Type().SZArray().Type(reflectedType, isValueType: false), _ =>
            {
            });
        var forwarded = metadata.AddMemberReference(assemblyType, metadata.GetOrAddString("GetForwardedTypes"),
            metadata.GetOrAddBlob(forwardedSignature));
        var instructions = new InstructionEncoder(new BlobBuilder());
        instructions.Call(executing);
        instructions.OpCode(ILOpCode.Callvirt);
        instructions.Token(forwarded);
        instructions.OpCode(ILOpCode.Ldlen);
        instructions.OpCode(ILOpCode.Conv_i4);
        instructions.OpCode(ILOpCode.Ret);
        var bodies = new BlobBuilder();
        var offset = new MethodBodyStreamEncoder(bodies).AddMethodBody(instructions);
        byte[] readSignature = [0, 0, 8];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(readSignature), offset, MetadataTokens.ParameterHandle(1));
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
