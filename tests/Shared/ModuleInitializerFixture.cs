using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds a module initializer whose public and private helpers initialize a selected method's copied static state.
/// </summary>
public static class ModuleInitializerFixture
{
    /// <summary>
    /// Reads the initialized state and optionally changes the result without changing the source initializer.
    /// </summary>
    /// <param name="edited">Whether to add one to the selected method's result.</param>
    /// <returns>The complete selected method body.</returns>
    public static string Method(bool edited) => """
        .method public static int32 Read() {
          ldsfld int32 Owner::Value
          ldsfld int32 Owner::Calls
          ldc.i4.s 100
          mul
          add
        """ + (edited ? "\nldc.i4.1\nadd" : "") + "\nret\n}";

    /// <summary>
    /// Creates a real assembly whose selected method returns 142 after its module initializer runs exactly once.
    /// </summary>
    /// <param name="typeInitializer">Whether the declaring type also initializes its field before the module helper updates it.</param>
    /// <param name="marker">An optional file to append to each time the module initializer executes.</param>
    /// <returns>The complete PE image.</returns>
    public static byte[] Create(bool typeInitializer, string? marker = null)
    {
        var metadata = new MetadataBuilder();
        var name = "ModuleInitializer" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = metadata.AddTypeReference(reference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        var firstHelper = typeInitializer ? 4 : 3;
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(2));
        metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Startup"), objectType,
            MetadataTokens.FieldDefinitionHandle(3), MetadataTokens.MethodDefinitionHandle(firstHelper));
        byte[] fieldSignature = [6, 8];
        var value = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static,
            metadata.GetOrAddString("Value"), metadata.GetOrAddBlob(fieldSignature));
        var calls = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static,
            metadata.GetOrAddString("Calls"), metadata.GetOrAddBlob(fieldSignature));
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        const MethodAttributes constructor = MethodAttributes.Private | MethodAttributes.Static
            | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var module = new InstructionEncoder(new BlobBuilder());
        if (marker is not null)
        {
            var file = metadata.AddTypeReference(reference, metadata.GetOrAddString("System.IO"), metadata.GetOrAddString("File"));
            byte[] signature = [0, 2, 1, 0x0e, 0x0e];
            var append = metadata.AddMemberReference(file, metadata.GetOrAddString("AppendAllText"), metadata.GetOrAddBlob(signature));
            module.LoadString(metadata.GetOrAddUserString(marker));
            module.LoadString(metadata.GetOrAddUserString("initialized\n"));
            module.Call(append);
        }

        module.Call(MetadataTokens.MethodDefinitionHandle(firstHelper));
        module.OpCode(ILOpCode.Ret);
        Method(".cctor", constructor, [0, 0, 1], module);
        var read = new InstructionEncoder(new BlobBuilder());
        read.OpCode(ILOpCode.Ldsfld);
        read.Token(value);
        read.OpCode(ILOpCode.Ldsfld);
        read.Token(calls);
        read.LoadConstantI4(100);
        read.OpCode(ILOpCode.Mul);
        read.OpCode(ILOpCode.Add);
        read.OpCode(ILOpCode.Ret);
        Method("Read", MethodAttributes.Public | MethodAttributes.Static, [0, 0, 8], read);
        if (typeInitializer)
        {
            var initializeType = new InstructionEncoder(new BlobBuilder());
            initializeType.LoadConstantI4(10);
            initializeType.OpCode(ILOpCode.Stsfld);
            initializeType.Token(value);
            initializeType.OpCode(ILOpCode.Ret);
            Method(".cctor", constructor, [0, 0, 1], initializeType);
        }

        var initialize = new InstructionEncoder(new BlobBuilder());
        initialize.OpCode(ILOpCode.Ldsfld);
        initialize.Token(value);
        initialize.Call(MetadataTokens.MethodDefinitionHandle(firstHelper + 1));
        initialize.OpCode(ILOpCode.Add);
        initialize.OpCode(ILOpCode.Stsfld);
        initialize.Token(value);
        initialize.OpCode(ILOpCode.Ldsfld);
        initialize.Token(calls);
        initialize.LoadConstantI4(1);
        initialize.OpCode(ILOpCode.Add);
        initialize.OpCode(ILOpCode.Stsfld);
        initialize.Token(calls);
        initialize.OpCode(ILOpCode.Ret);
        Method("Initialize", MethodAttributes.Public | MethodAttributes.Static, [0, 0, 1], initialize);
        var increment = new InstructionEncoder(new BlobBuilder());
        increment.LoadConstantI4(typeInitializer ? 32 : 42);
        increment.OpCode(ILOpCode.Ret);
        Method("Increment", MethodAttributes.Private | MethodAttributes.Static, [0, 0, 8], increment);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();

        void Method(string methodName, MethodAttributes attributes, byte[] signature, InstructionEncoder instructions)
            => metadata.AddMethodDefinition(attributes, MethodImplAttributes.IL, metadata.GetOrAddString(methodName),
                metadata.GetOrAddBlob(signature), bodies.AddMethodBody(instructions), MetadataTokens.ParameterHandle(1));
    }
}
