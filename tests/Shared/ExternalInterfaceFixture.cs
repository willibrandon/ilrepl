using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits a public external base whose private interface must retain its identity in a copied derived type.
/// </summary>
public static class ExternalInterfaceFixture
{
    /// <summary>
    /// Revises the selected method while retaining its cast, interface call, or inherited member access.
    /// </summary>
    /// <param name="operation">The cast, interface call, or inherited member operation.</param>
    /// <param name="generic">Whether the interface has a private type argument.</param>
    /// <returns>The edited method declaration.</returns>
    public static string EditedMethod(string operation, bool generic)
    {
        var contract = generic ? "IHidden`1<Payload>" : "IHidden";
        var body = operation switch
        {
            "field" => "ldfld int32 ExternalBase::Number",
            "internal" => "call instance int32 ExternalBase::ReadValue()",
            "isinst" or "base-isinst" => "isinst " + contract + "\nldnull\ncgt.un\nldc.i4.s 42\nmul",
            "castclass" => "castclass " + contract + "\ncallvirt instance int32 " + contract + "::Value()",
            _ => "callvirt instance int32 " + contract + "::Value()"
        };
        var owner = operation == "base-isinst" ? "ExternalBase" : "Owner";
        return ".method public static int32 Probe() {\nnewobj instance void " + owner + "::.ctor()\n"
            + body + "\nldc.i4.1\nadd\nret\n}";
    }

    /// <summary>
    /// Builds real interface casts and calls, optionally using a private generic argument or explicit reimplementation.
    /// </summary>
    /// <param name="operation">The cast, interface call, or inherited member operation.</param>
    /// <param name="generic">Whether the interface has a private type argument.</param>
    /// <param name="reimplement">Whether the derived type explicitly reimplements the inherited interface.</param>
    /// <param name="ownerArgument">Whether the interface requires the original owner as its generic argument.</param>
    /// <returns>The assembly name and complete PE image.</returns>
    public static (string Name, byte[] Image) Create(string operation, bool generic, bool reimplement, bool ownerArgument = false)
    {
        var metadata = new MetadataBuilder();
        var name = "ExternalInterfaces" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = metadata.AddTypeReference(reference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        var objectConstructor = metadata.AddMemberReference(objectType, metadata.GetOrAddString(".ctor"), Blob([0x20, 0, 1]));
        var firstField = MetadataTokens.FieldDefinitionHandle(1);
        var firstParameter = MetadataTokens.ParameterHandle(1);
        var field = operation == "field" ? metadata.AddFieldDefinition(FieldAttributes.Assembly,
            metadata.GetOrAddString("Number"), Blob([6, 8])) : default;
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            firstField, MetadataTokens.MethodDefinitionHandle(1));
        var contract = metadata.AddTypeDefinition(TypeAttributes.Interface | TypeAttributes.Abstract,
            default, metadata.GetOrAddString(generic ? "IHidden`1" : "IHidden"), default,
            firstField, MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("Payload"), objectType,
            firstField, MetadataTokens.MethodDefinitionHandle(2));
        var parent = metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("ExternalBase"), objectType,
            firstField, MetadataTokens.MethodDefinitionHandle(2));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Owner"), parent,
            field.IsNil ? firstField : MetadataTokens.FieldDefinitionHandle(2), MetadataTokens.MethodDefinitionHandle(4));
        EntityHandle implemented = contract;
        if (generic)
        {
            metadata.AddGenericParameter(contract, 0, metadata.GetOrAddString("T"), 0);
            implemented = metadata.AddTypeSpecification(Blob([0x15, 0x12, 8, 1, 0x12, ownerArgument ? (byte)20 : (byte)12]));
        }

        metadata.AddInterfaceImplementation(parent, implemented);
        if (reimplement) metadata.AddInterfaceImplementation(owner, implemented);
        var value = metadata.AddMemberReference(implemented, metadata.GetOrAddString("Value"), Blob([0x20, 0, 8]));
        metadata.AddMethodImplementation(parent, MetadataTokens.MethodDefinitionHandle(3), value);
        if (reimplement) metadata.AddMethodImplementation(owner, MetadataTokens.MethodDefinitionHandle(6), value);
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        var abstractMethod = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Abstract;
        metadata.AddMethodDefinition(abstractMethod,
            MethodImplAttributes.IL, metadata.GetOrAddString("Value"), Blob([0x20, 0, 8]), -1, firstParameter);
        Constructor(objectConstructor);
        Implementation(42);
        Constructor(MetadataTokens.MethodDefinitionHandle(2));
        var probe = new InstructionEncoder(new BlobBuilder());
        probe.OpCode(ILOpCode.Newobj);
        probe.Token(MetadataTokens.MethodDefinitionHandle(operation == "base-isinst" ? 2 : 4));
        if (operation == "field")
        {
            probe.OpCode(ILOpCode.Ldfld);
            probe.Token(field);
        }
        else if (operation == "internal")
        {
            probe.Call(MetadataTokens.MethodDefinitionHandle(3));
        }
        else if (operation is "isinst" or "base-isinst")
        {
            probe.OpCode(ILOpCode.Isinst);
            probe.Token(implemented);
            probe.OpCode(ILOpCode.Ldnull);
            probe.OpCode(ILOpCode.Cgt_un);
            probe.LoadConstantI4(42);
            probe.OpCode(ILOpCode.Mul);
        }
        else
        {
            if (operation == "castclass")
            {
                probe.OpCode(ILOpCode.Castclass);
                probe.Token(implemented);
            }

            probe.OpCode(ILOpCode.Callvirt);
            probe.Token(value);
        }

        probe.OpCode(ILOpCode.Ret);
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Probe"), Blob([0, 0, 8]), bodies.AddMethodBody(probe), firstParameter);
        if (reimplement) Implementation(43);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return (name, image.ToArray());

        BlobHandle Blob(byte[] bytes) => metadata.GetOrAddBlob(bytes);

        void Constructor(EntityHandle target)
        {
            var il = new InstructionEncoder(new BlobBuilder());
            il.LoadArgument(0);
            il.Call(target);
            if (!field.IsNil && target == objectConstructor)
            {
                il.LoadArgument(0);
                il.LoadConstantI4(42);
                il.OpCode(ILOpCode.Stfld);
                il.Token(field);
            }

            il.OpCode(ILOpCode.Ret);
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                MethodImplAttributes.IL, metadata.GetOrAddString(".ctor"), Blob([0x20, 0, 1]), bodies.AddMethodBody(il), firstParameter);
        }

        void Implementation(int result)
        {
            var il = new InstructionEncoder(new BlobBuilder());
            il.LoadConstantI4(result);
            il.OpCode(ILOpCode.Ret);
            var access = operation == "internal" ? MethodAttributes.Assembly : MethodAttributes.Private;
            metadata.AddMethodDefinition(access | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final,
                MethodImplAttributes.IL, metadata.GetOrAddString("ReadValue"), Blob([0x20, 0, 8]),
                bodies.AddMethodBody(il), firstParameter);
        }
    }
}
