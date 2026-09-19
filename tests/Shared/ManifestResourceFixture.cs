using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds real assemblies with an embedded resource and methods that inspect it through each resource API.
/// </summary>
public static class ManifestResourceFixture
{
    /// <summary>
    /// Emits a resource containing 42 and a selected method that observes its bytes, name, or metadata.
    /// </summary>
    /// <param name="api">The names, info, stream, or typed stream API.</param>
    /// <returns>The complete portable executable image.</returns>
    public static byte[] Create(string api)
    {
        var metadata = new MetadataBuilder();
        var name = "Resources" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        TypeReferenceHandle Type(string space, string type) => metadata.AddTypeReference(reference,
            metadata.GetOrAddString(space), metadata.GetOrAddString(type));
        var objectType = Type("System", "Object");
        var assemblyType = Type("System.Reflection", "Assembly");
        var reflectedType = Type("System", "Type");
        var streamType = Type("System.IO", "Stream");
        var resourceInfo = Type("System.Reflection", "ManifestResourceInfo");
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("Resources"),
            metadata.GetOrAddString("Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var executingSignature = new BlobBuilder();
        new BlobEncoder(executingSignature).MethodSignature().Parameters(0,
            result => result.Type().Type(assemblyType, isValueType: false), _ =>
            {
            });
        var executing = metadata.AddMemberReference(assemblyType, metadata.GetOrAddString("GetExecutingAssembly"),
            metadata.GetOrAddBlob(executingSignature));
        var signature = new BlobBuilder();
        var stream = api is "stream" or "typed stream";
        var method = stream ? "GetManifestResourceStream" : api == "names" ? "GetManifestResourceNames" : "GetManifestResourceInfo";
        new BlobEncoder(signature).MethodSignature(isInstanceMethod: true).Parameters(api == "names" ? 0 : api == "typed stream" ? 2 : 1,
            result =>
            {
                if (api == "names")
                {
                    result.Type().SZArray().String();
                }
                else
                {
                    result.Type().Type(stream ? streamType : resourceInfo, isValueType: false);
                }
            }, parameters =>
            {
                if (api == "typed stream")
                {
                    parameters.AddParameter().Type().Type(reflectedType, isValueType: false);
                }

                if (api != "names")
                {
                    parameters.AddParameter().Type().String();
                }
            });
        var inspection = metadata.AddMemberReference(assemblyType, metadata.GetOrAddString(method), metadata.GetOrAddBlob(signature));
        var instructions = new InstructionEncoder(new BlobBuilder());
        instructions.Call(executing);
        if (api == "typed stream")
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(owner);
            var fromHandle = new BlobBuilder();
            new BlobEncoder(fromHandle).MethodSignature().Parameters(1,
                result => result.Type().Type(reflectedType, isValueType: false),
                parameters => parameters.AddParameter().Type().Type(Type("System", "RuntimeTypeHandle"), isValueType: true));
            instructions.Call(metadata.AddMemberReference(reflectedType, metadata.GetOrAddString("GetTypeFromHandle"),
                metadata.GetOrAddBlob(fromHandle)));
        }

        if (api != "names")
        {
            instructions.LoadString(metadata.GetOrAddUserString(api == "typed stream" ? "payload" : "Resources.payload"));
        }

        instructions.OpCode(ILOpCode.Callvirt);
        instructions.Token(inspection);
        if (stream)
        {
            var read = new BlobBuilder();
            new BlobEncoder(read).MethodSignature(isInstanceMethod: true).Parameters(0, result => result.Type().Int32(), _ =>
            {
            });
            instructions.OpCode(ILOpCode.Callvirt);
            instructions.Token(metadata.AddMemberReference(streamType, metadata.GetOrAddString("ReadByte"), metadata.GetOrAddBlob(read)));
        }
        else
        {
            if (api == "names")
            {
                instructions.OpCode(ILOpCode.Ldlen);
                instructions.OpCode(ILOpCode.Conv_i4);
            }
            else
            {
                instructions.OpCode(ILOpCode.Ldnull);
                instructions.OpCode(ILOpCode.Cgt_un);
            }

            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Mul);
        }

        instructions.OpCode(ILOpCode.Ret);
        var bodies = new BlobBuilder();
        var offset = new MethodBodyStreamEncoder(bodies).AddMethodBody(instructions);
        byte[] readSignature = [0, 0, 8];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(readSignature), offset, MetadataTokens.ParameterHandle(1));
        var resources = new BlobBuilder();
        resources.WriteInt32(3);
        byte[] payload = [42, 17, 255];
        resources.WriteBytes(payload);
        metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString("Resources.payload"), default, 0);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, managedResources: resources, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
