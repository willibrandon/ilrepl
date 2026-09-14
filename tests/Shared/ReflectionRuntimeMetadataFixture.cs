using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds member enumeration over a real type containing runtime declarations without IL bodies.
/// </summary>
public static class ReflectionRuntimeMetadataFixture
{
    /// <summary>
    /// Emits an internal-call declaration and optional calls that require its unavailable implementation.
    /// </summary>
    /// <param name="virtualMethod">Whether the runtime declaration is virtual.</param>
    /// <param name="runtime">Whether the declaration also uses the runtime code type.</param>
    /// <param name="call">The selected method, a discovered helper, or neither requires the runtime body.</param>
    /// <returns>The complete PE image.</returns>
    public static byte[] Create(bool virtualMethod, bool runtime, string call = "none")
    {
        var metadata = new MetadataBuilder();
        var name = "ReflectedRuntime" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = Type("System", "Object");
        var typeType = Type("System", "Type");
        Type("System", "RuntimeTypeHandle");
        Type("System.Reflection", "BindingFlags");
        Type("System.Reflection", "MethodInfo");
        var fromHandle = metadata.AddMemberReference(typeType, metadata.GetOrAddString("GetTypeFromHandle"), Blob(0, 1, 0x12, 9, 0x11, 13));
        var methods = metadata.AddMemberReference(typeType, metadata.GetOrAddString("GetMethods"), Blob(0x20, 1, 0x1d, 0x12, 21, 0x11, 17));
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        var read = new InstructionEncoder(new BlobBuilder());
        read.OpCode(ILOpCode.Ldtoken);
        read.Token(owner);
        read.Call(fromHandle);
        read.LoadConstantI4(46);
        read.OpCode(ILOpCode.Callvirt);
        read.Token(methods);
        read.OpCode(ILOpCode.Ldlen);
        read.OpCode(ILOpCode.Conv_i4);
        read.OpCode(ILOpCode.Ret);
        if (call == "selected")
        {
            read.Call(MetadataTokens.MethodDefinitionHandle(2));
            read.OpCode(ILOpCode.Ret);
        }

        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), Blob(0, 0, 8), bodies.AddMethodBody(read), MetadataTokens.ParameterHandle(1));
        var attributes = virtualMethod ? MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.NewSlot
            : MethodAttributes.Private | MethodAttributes.Static;
        metadata.AddMethodDefinition(attributes, MethodImplAttributes.InternalCall | (runtime ? MethodImplAttributes.Runtime : 0),
            metadata.GetOrAddString("Native"), virtualMethod ? Blob(0x20, 0, 8) : Blob(0, 0, 8), -1, MetadataTokens.ParameterHandle(1));
        var helper = new InstructionEncoder(new BlobBuilder());
        helper.LoadConstantI4(42);
        helper.OpCode(ILOpCode.Ret);
        if (call == "helper")
        {
            helper.Call(MetadataTokens.MethodDefinitionHandle(2));
            helper.OpCode(ILOpCode.Ret);
        }

        metadata.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Helper"), Blob(0, 0, 8), bodies.AddMethodBody(helper), MetadataTokens.ParameterHandle(1));
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();

        BlobHandle Blob(params byte[] bytes) => metadata.GetOrAddBlob(bytes);

        TypeReferenceHandle Type(string space, string typeName) => metadata.AddTypeReference(reference,
            metadata.GetOrAddString(space), metadata.GetOrAddString(typeName));
    }
}
