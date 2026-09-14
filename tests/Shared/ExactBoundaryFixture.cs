using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits external members whose exact signatures contain a nominal type hidden by ordinary reflection projections.
/// </summary>
public static class ExactBoundaryFixture
{
    /// <summary>
    /// Builds a callable source method and an external helper or field with the requested exact signature.
    /// </summary>
    /// <param name="kind">The parameter, return, or field signature shape.</param>
    /// <param name="copiedType">Whether the signature refers to the copied owner or the unchanged external type.</param>
    /// <returns>The complete PE image.</returns>
    public static byte[] Create(string kind, bool copiedType)
    {
        var metadata = new MetadataBuilder();
        var name = "ExactBoundary" + Guid.NewGuid().ToString("N");
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
        metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("External"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(2));
        var nominal = copiedType ? (byte)8 : (byte)12;
        byte[] signature = kind switch
        {
            "parameter-modifier" => [0x20, nominal, 8],
            "return-modifier" or "field-modifier" => [0x1f, nominal, 8],
            "parameter-pointer-modifier" => [0x0f, 0x20, nominal, 8],
            "parameter-function-parameter" => [0x1b, 0, 1, 1, 0x12, nominal],
            "parameter-function-modifier" => [0x1b, 0, 0, 0x20, nominal, 8],
            "field-array-modifier" => [0x1d, 0x20, nominal, 8],
            _ => [0x1b, 0, 0, 0x12, nominal],
        };
        var field = kind.StartsWith("field", StringComparison.Ordinal);
        var parameter = kind.StartsWith("parameter", StringComparison.Ordinal);
        var functionReturn = kind == "return-function";
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        var read = new InstructionEncoder(new BlobBuilder());
        if (field)
        {
            var handle = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static,
                metadata.GetOrAddString("Value"), Blob([6, .. signature]));
            read.OpCode(ILOpCode.Ldsfld);
            read.Token(handle);
            read.OpCode(ILOpCode.Pop);
            read.LoadConstantI4(42);
        }
        else
        {
            if (parameter)
            {
                read.LoadConstantI4(kind == "parameter-modifier" ? 42 : 0);
                if (kind != "parameter-modifier") read.OpCode(ILOpCode.Conv_u);
            }

            read.Call(MetadataTokens.MethodDefinitionHandle(2));
            if (functionReturn)
            {
                read.OpCode(ILOpCode.Pop);
                read.LoadConstantI4(42);
            }
        }

        read.OpCode(ILOpCode.Ret);
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), Blob([0, 0, 8]), bodies.AddMethodBody(read), MetadataTokens.ParameterHandle(1));
        var helper = new InstructionEncoder(new BlobBuilder());
        helper.LoadConstantI4(functionReturn ? 0 : 42);
        if (functionReturn) helper.OpCode(ILOpCode.Conv_u);
        helper.OpCode(ILOpCode.Ret);
        byte[] helperSignature = parameter ? [0, 1, 8, .. signature] : field ? [0, 0, 8] : [0, 0, .. signature];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Transfer"), Blob(helperSignature), bodies.AddMethodBody(helper), MetadataTokens.ParameterHandle(1));
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();

        BlobHandle Blob(byte[] bytes) => metadata.GetOrAddBlob(bytes);
    }
}
