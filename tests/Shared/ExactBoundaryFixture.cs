using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits members whose exact signatures hide nominal types in custom modifiers and function pointers.
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
        return Build(kind, copiedType, "ExactBoundary" + Guid.NewGuid().ToString("N"), null, false);
    }

    /// <summary>
    /// Builds separate source and helper assemblies with exact signatures referencing each other's nominal types.
    /// </summary>
    /// <param name="kind">The parameter, return, or field signature shape.</param>
    /// <param name="copiedType">Whether the helper signature refers to the source owner or the unchanged helper type.</param>
    /// <returns>The source and helper PE images, with no fabricated runtime dependency.</returns>
    public static (byte[] Source, byte[] Helper) CreateExternal(string kind, bool copiedType)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var source = "ExactBoundarySource" + suffix;
        var helper = "ExactBoundaryHelper" + suffix;
        return (Build(kind, copiedType, source, helper, false), Build(kind, copiedType, helper, source, true));
    }

    private static byte[] Build(string kind, bool copiedType, string name, string? otherName, bool helperOnly)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = metadata.AddTypeReference(reference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString(helperOnly ? "External" : "Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        EntityHandle external = MetadataTokens.TypeDefinitionHandle(3);
        if (otherName is null)
        {
            metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("External"), objectType,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(2));
        }
        else
        {
            var other = metadata.AddAssemblyReference(metadata.GetOrAddString(otherName), new Version(1, 0, 0, 0),
                default, default, 0, default);
            external = metadata.AddTypeReference(other, default, metadata.GetOrAddString(helperOnly ? "Owner" : "External"));
        }

        var nominal = otherName is null ? copiedType ? (byte)8 : (byte)12
            : copiedType != helperOnly ? (byte)8 : (byte)9;
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
        byte[] helperSignature = parameter ? [0, 1, 8, .. signature] : field ? [0, 0, 8] : [0, 0, .. signature];
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        var read = new InstructionEncoder(new BlobBuilder());
        if (field)
        {
            EntityHandle handle = otherName is null || helperOnly
                ? metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static,
                    metadata.GetOrAddString("Value"), Blob([6, .. signature]))
                : metadata.AddMemberReference(external, metadata.GetOrAddString("Value"), Blob([6, .. signature]));
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
                if (kind != "parameter-modifier")
                {
                    read.OpCode(ILOpCode.Conv_u);
                }
            }

            read.Call(otherName is null || helperOnly ? MetadataTokens.MethodDefinitionHandle(helperOnly ? 1 : 2)
                : metadata.AddMemberReference(external, metadata.GetOrAddString("Transfer"), Blob(helperSignature)));
            if (functionReturn)
            {
                read.OpCode(ILOpCode.Pop);
                read.LoadConstantI4(42);
            }
        }

        read.OpCode(ILOpCode.Ret);
        if (!helperOnly)
        {
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString("Read"), Blob([0, 0, 8]), bodies.AddMethodBody(read), MetadataTokens.ParameterHandle(1));
        }

        var helper = new InstructionEncoder(new BlobBuilder());
        helper.LoadConstantI4(functionReturn ? 0 : 42);
        if (functionReturn)
        {
            helper.OpCode(ILOpCode.Conv_u);
        }

        helper.OpCode(ILOpCode.Ret);
        if (otherName is null || helperOnly)
        {
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString("Transfer"), Blob(helperSignature), bodies.AddMethodBody(helper),
                MetadataTokens.ParameterHandle(1));
        }

        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();

        BlobHandle Blob(byte[] bytes) => metadata.GetOrAddBlob(bytes);
    }
}
