using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds real private delegates with runtime constructors and invocation methods for copied-family tests.
/// </summary>
public static class DelegateMetadataFixture
{
    /// <summary>
    /// Writes a method that constructs, stores, and invokes a delegate to a private helper.
    /// </summary>
    /// <param name="generic">Whether the delegate closes its result type with a generic argument.</param>
    /// <returns>The complete PE image.</returns>
    public static byte[] Create(bool generic)
    {
        var metadata = new MetadataBuilder();
        var name = "Delegate" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = Type("System", "Object");
        var delegateType = Type("System", "MulticastDelegate");
        Type("System", "AsyncCallback");
        Type("System", "IAsyncResult");
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var callback = metadata.AddTypeDefinition(TypeAttributes.NestedPrivate | TypeAttributes.Sealed, default,
            metadata.GetOrAddString(generic ? "Callback`1" : "Callback"), delegateType,
            MetadataTokens.FieldDefinitionHandle(2), MetadataTokens.MethodDefinitionHandle(3));
        metadata.AddNestedType(callback, owner);
        if (generic)
        {
            metadata.AddGenericParameter(callback, GenericParameterAttributes.None, metadata.GetOrAddString("T"), 0);
        }

        byte[] callbackSignature = generic ? [0x15, 0x12, 12, 1, 8] : [0x12, 12];
        var field = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, metadata.GetOrAddString("Last"),
            metadata.GetOrAddBlob(new byte[] { 6 }.Concat(callbackSignature).ToArray()));
        EntityHandle constructed = generic ? metadata.AddTypeSpecification(metadata.GetOrAddBlob(callbackSignature)) : callback;
        var constructor = metadata.AddMemberReference(constructed, metadata.GetOrAddString(".ctor"), Blob(0x20, 2, 1, 0x1c, 0x18));
        var invokeSignature = generic ? Blob(0x20, 0, 0x13, 0) : Blob(0x20, 0, 8);
        var invoke = metadata.AddMemberReference(constructed, metadata.GetOrAddString("Invoke"), invokeSignature);
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        var read = new InstructionEncoder(new BlobBuilder());
        read.OpCode(ILOpCode.Ldnull);
        read.OpCode(ILOpCode.Ldftn);
        read.Token(MetadataTokens.MethodDefinitionHandle(2));
        read.OpCode(ILOpCode.Newobj);
        read.Token(constructor);
        read.OpCode(ILOpCode.Dup);
        read.OpCode(ILOpCode.Stsfld);
        read.Token(field);
        read.OpCode(ILOpCode.Callvirt);
        read.Token(invoke);
        read.OpCode(ILOpCode.Ret);
        Method("Read", MethodAttributes.Public | MethodAttributes.Static, Blob(0, 0, 8), bodies.AddMethodBody(read));
        var target = new InstructionEncoder(new BlobBuilder());
        target.LoadConstantI4(42);
        target.OpCode(ILOpCode.Ret);
        Method("Target", MethodAttributes.Private | MethodAttributes.Static, Blob(0, 0, 8), bodies.AddMethodBody(target));
        Method(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            Blob(0x20, 2, 1, 0x1c, 0x18), -1);
        Method("Invoke", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot, invokeSignature, -1);
        Method("BeginInvoke", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot,
            Blob(0x20, 2, 0x12, 17, 0x12, 13, 0x1c), -1);
        Method("EndInvoke", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot,
            generic ? Blob(0x20, 1, 0x13, 0, 0x12, 17) : Blob(0x20, 1, 8, 0x12, 17), -1);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();

        BlobHandle Blob(params byte[] bytes) => metadata.GetOrAddBlob(bytes);

        TypeReferenceHandle Type(string space, string typeName) => metadata.AddTypeReference(reference,
            metadata.GetOrAddString(space), metadata.GetOrAddString(typeName));

        void Method(string methodName, MethodAttributes flags, BlobHandle signature, int body) =>
            metadata.AddMethodDefinition(flags, body < 0 ? MethodImplAttributes.Runtime : MethodImplAttributes.IL,
                metadata.GetOrAddString(methodName), signature, body, MetadataTokens.ParameterHandle(1));
    }
}
