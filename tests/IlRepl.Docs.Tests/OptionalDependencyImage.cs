using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Docs.Tests;

/// <summary>
/// Creates a real library with an optional dependency that is absent from the browser runtime.
/// </summary>
internal static class OptionalDependencyImage
{
    /// <summary>
    /// Emits a working entry and a separate entry that calls the missing library.
    /// </summary>
    /// <returns>The library's portable executable image.</returns>
    public static byte[] Create()
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("OptionalLibrary"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("OptionalLibrary"), new Version(1, 0, 0, 0), default, default, 0,
            AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var coreReference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = metadata.AddTypeReference(coreReference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        var missing = metadata.AddAssemblyReference(metadata.GetOrAddString("MissingOptionalLibrary"), new Version(1, 0, 0, 0),
            default, default, 0, default);
        var missingType = metadata.AddTypeReference(missing, metadata.GetOrAddString("N"), metadata.GetOrAddString("Optional"));
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, metadata.GetOrAddString("N"),
            metadata.GetOrAddString("Library"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature().Parameters(0, result => result.Type().Int32(), _ => { });
        var signatureHandle = metadata.GetOrAddBlob(signature);
        var missingMethod = metadata.AddMemberReference(missingType, metadata.GetOrAddString("Read"), signatureHandle);
        var bodies = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(bodies);
        var code = new BlobBuilder();
        var instructions = new InstructionEncoder(code);
        instructions.LoadConstantI4(41);
        instructions.OpCode(ILOpCode.Ret);
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), signatureHandle, encoder.AddMethodBody(instructions), MetadataTokens.ParameterHandle(1));
        code = new BlobBuilder();
        instructions = new InstructionEncoder(code);
        instructions.Call(missingMethod);
        instructions.OpCode(ILOpCode.Ret);
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Optional"), signatureHandle, encoder.AddMethodBody(instructions), MetadataTokens.ParameterHandle(1));
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
