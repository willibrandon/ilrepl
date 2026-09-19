using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Builds executable property and event fixtures with accessor semantics that C# cannot declare.
/// </summary>
public static class AccessorMetadataFixture
{
    /// <summary>
    /// Writes a real assembly with standard accessors, several other accessors, and an attribute-only type dependency.
    /// </summary>
    /// <param name="eventMember">Whether to associate the methods with an event instead of a property.</param>
    /// <param name="standard">Whether the selected other accessor calls the standard accessors.</param>
    /// <param name="extra">Whether the selected accessor calls a second other accessor.</param>
    /// <param name="privateAccessor">Whether the selected accessor is private.</param>
    /// <returns>The complete managed PE image.</returns>
    public static byte[] Create(bool eventMember, bool standard, bool extra, bool privateAccessor)
    {
        var metadata = new MetadataBuilder();
        var assemblyName = "AccessorFixture" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(assemblyName), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(assemblyName), new Version(1, 0, 0, 0), default, default, 0,
            AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var coreReference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        var objectType = metadata.AddTypeReference(coreReference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        var actionType = metadata.AddTypeReference(coreReference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Action"));
        var attributeType = metadata.AddTypeReference(coreReference, metadata.GetOrAddString("System"),
            metadata.GetOrAddString("Attribute"));
        metadata.AddTypeReference(coreReference, metadata.GetOrAddString("System"), metadata.GetOrAddString("Type"));
        var attributeConstructor = metadata.AddMemberReference(attributeType, metadata.GetOrAddString(".ctor"), Blob(0x20, 0, 1));
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        const MethodAttributes methodFlags = MethodAttributes.Static | MethodAttributes.HideBySig;
        Method("Read", (privateAccessor ? MethodAttributes.Private : MethodAttributes.Public) | methodFlags, Blob(0, 0, 8), il =>
        {
            if (standard)
            {
                il.OpCode(ILOpCode.Ldnull);
                il.Call(MetadataTokens.MethodDefinitionHandle(eventMember ? 6 : 5));
                if (eventMember)
                {
                    il.OpCode(ILOpCode.Ldnull);
                    il.Call(MetadataTokens.MethodDefinitionHandle(7));
                    il.Call(MetadataTokens.MethodDefinitionHandle(8));
                }
                else
                {
                    il.Call(MetadataTokens.MethodDefinitionHandle(4));
                    il.OpCode(ILOpCode.Pop);
                }
            }

            if (extra)
            {
                il.Call(MetadataTokens.MethodDefinitionHandle(2));
                il.OpCode(ILOpCode.Pop);
            }

            il.LoadConstantI4(42);
        });
        Method("Extra", MethodAttributes.Private | methodFlags, Blob(0, 0, 8), il => il.LoadConstantI4(7));
        Method("Unused", MethodAttributes.Public | methodFlags, Blob(0, 0, 8), il => il.LoadConstantI4(13));
        Method("get_Value", MethodAttributes.Public | MethodAttributes.SpecialName | methodFlags,
            Blob(0, 0, 0x12, 12), il => il.OpCode(ILOpCode.Ldnull));
        Method("set_Value", MethodAttributes.Public | MethodAttributes.SpecialName | methodFlags, Blob(0, 1, 1, 0x12, 12), _ =>
        {
        });
        Method("add_Changed", MethodAttributes.Public | MethodAttributes.SpecialName | methodFlags, Blob(0, 1, 1, 0x12, 9), _ =>
        {
        });
        Method("remove_Changed", MethodAttributes.Public | MethodAttributes.SpecialName | methodFlags, Blob(0, 1, 1, 0x12, 9), _ =>
        {
        });
        Method("Raise", MethodAttributes.Public | MethodAttributes.SpecialName | methodFlags, Blob(0, 0, 1), _ =>
        {
        });
        var tagConstructor = Method(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            Blob(0x20, 1, 1, 0x12, 17), il =>
            {
                il.LoadArgument(0);
                il.Call(attributeConstructor);
            });
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, default, metadata.GetOrAddString("Owner"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var marker = metadata.AddTypeDefinition(TypeAttributes.NestedPrivate, default, metadata.GetOrAddString("Marker"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), tagConstructor);
        var tag = metadata.AddTypeDefinition(TypeAttributes.NestedPrivate | TypeAttributes.Sealed, default,
            metadata.GetOrAddString("TagAttribute"),
            attributeType, MetadataTokens.FieldDefinitionHandle(1), tagConstructor);
        metadata.AddNestedType(marker, owner);
        metadata.AddNestedType(tag, owner);

        EntityHandle member;
        if (eventMember)
        {
            var entry = metadata.AddEvent(EventAttributes.SpecialName, metadata.GetOrAddString("Changed"), actionType);
            metadata.AddEventMap(owner, entry);
            metadata.AddMethodSemantics(entry, MethodSemanticsAttributes.Adder, MetadataTokens.MethodDefinitionHandle(6));
            metadata.AddMethodSemantics(entry, MethodSemanticsAttributes.Remover, MetadataTokens.MethodDefinitionHandle(7));
            metadata.AddMethodSemantics(entry, MethodSemanticsAttributes.Raiser, MetadataTokens.MethodDefinitionHandle(8));
            member = entry;
        }
        else
        {
            var property = metadata.AddProperty(PropertyAttributes.SpecialName, metadata.GetOrAddString("Value"), Blob(8, 0, 0x12, 12));
            metadata.AddPropertyMap(owner, property);
            metadata.AddMethodSemantics(property, MethodSemanticsAttributes.Getter, MetadataTokens.MethodDefinitionHandle(4));
            metadata.AddMethodSemantics(property, MethodSemanticsAttributes.Setter, MetadataTokens.MethodDefinitionHandle(5));
            member = property;
        }

        foreach (var row in new[] { 1, 2, 3 })
        {
            metadata.AddMethodSemantics(member, MethodSemanticsAttributes.Other, MetadataTokens.MethodDefinitionHandle(row));
        }

        var attribute = new BlobBuilder();
        attribute.WriteUInt16(1);
        attribute.WriteSerializedString("Owner+Marker, " + assemblyName);
        attribute.WriteUInt16(0);
        metadata.AddCustomAttribute(member, tagConstructor, metadata.GetOrAddBlob(attribute));
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();

        BlobHandle Blob(params byte[] bytes) => metadata.GetOrAddBlob(bytes);

        MethodDefinitionHandle Method(string name, MethodAttributes flags, BlobHandle signature, Action<InstructionEncoder> emit)
        {
            var il = new InstructionEncoder(new BlobBuilder());
            emit(il);
            il.OpCode(ILOpCode.Ret);
            return metadata.AddMethodDefinition(flags, MethodImplAttributes.IL, metadata.GetOrAddString(name), signature,
                bodies.AddMethodBody(il), MetadataTokens.ParameterHandle(1));
        }
    }
}
