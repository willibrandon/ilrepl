using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits public same-assembly helper graphs whose shared state must follow the selected method's copied context.
/// </summary>
public static class PublicHelperFixture
{
    /// <summary>
    /// Builds a source PE with direct, transitive, generic, callback, initializer, revisited or stateless public helpers.
    /// </summary>
    /// <param name="shape">The selected helper call, signature, initializer, state or stateless control route.</param>
    /// <returns>The complete actual assembly image.</returns>
    public static byte[] Create(string shape)
    {
        var metadata = new MetadataBuilder();
        var name = "PublicHelper" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var core = typeof(object).Assembly.GetName();
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        TypeReferenceHandle Type(string space, string type) => metadata.AddTypeReference(reference,
            metadata.GetOrAddString(space), metadata.GetOrAddString(type));
        var objectType = Type("System", "Object");
        var func = Type("System", "Func`1");
        var callbackSignature = new BlobBuilder();
        new BlobEncoder(callbackSignature).TypeSpecificationSignature().GenericInstantiation(func, 1, isValueType: false)
            .AddArgument().Int32();
        var callbackType = metadata.AddTypeSpecification(metadata.GetOrAddBlob(callbackSignature));
        var owner = MetadataTokens.TypeDefinitionHandle(2);
        var helper = MetadataTokens.TypeDefinitionHandle(3);
        var state = MetadataTokens.FieldDefinitionHandle(1);
        var cached = MetadataTokens.FieldDefinitionHandle(2);
        var otherState = MetadataTokens.FieldDefinitionHandle(3);
        var read = MetadataTokens.MethodDefinitionHandle(1);
        var readState = MetadataTokens.MethodDefinitionHandle(2);
        var fetch = MetadataTokens.MethodDefinitionHandle(4);
        var otherFetch = MetadataTokens.MethodDefinitionHandle(6);
        var bridgePublish = MetadataTokens.MethodDefinitionHandle(7);
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            state, read);
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("PublicContext"), metadata.GetOrAddString("Owner"),
            objectType, state, read);
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("PublicContext"), metadata.GetOrAddString("Helper"),
            objectType, cached, fetch);
        metadata.AddTypeDefinition(shape == "revisit" ? TypeAttributes.NotPublic : TypeAttributes.Public,
            metadata.GetOrAddString("PublicContext"), metadata.GetOrAddString("Other"), objectType, otherState, otherFetch);
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("PublicContext"), metadata.GetOrAddString("Bridge"),
            objectType, MetadataTokens.FieldDefinitionHandle(4), bridgePublish);
        byte[] fieldSignature = [0x06, 0x08];
        metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, metadata.GetOrAddString("State"),
            metadata.GetOrAddBlob(fieldSignature));
        metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, metadata.GetOrAddString("Cached"),
            metadata.GetOrAddBlob(fieldSignature));
        metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, metadata.GetOrAddString("State"),
            metadata.GetOrAddBlob(fieldSignature));
        var bodies = new BlobBuilder();
        var stream = new MethodBodyStreamEncoder(bodies);
        BlobHandle Signature(
            bool returnsVoid = false,
            bool parameter = false,
            bool generic = false,
            bool ownerParameter = false,
            bool callback = false,
            bool text = false)
        {
            var blob = new BlobBuilder();
            new BlobEncoder(blob).MethodSignature(genericParameterCount: generic ? 1 : 0).Parameters(parameter ? 1 : 0,
                result =>
                {
                    if (returnsVoid)
                    {
                        result.Void();
                    }
                    else if (text)
                    {
                        result.Type().String();
                    }
                    else if (callback)
                    {
                        result.Type().GenericInstantiation(func, 1, isValueType: false).AddArgument().Int32();
                    }
                    else
                    {
                        result.Type().Int32();
                    }
                }, parameters =>
                {
                    if (!parameter)
                    {
                        return;
                    }

                    var type = parameters.AddParameter().Type();
                    if (generic)
                    {
                        type.GenericMethodTypeParameter(0);
                    }
                    else if (ownerParameter)
                    {
                        type.Type(owner, isValueType: false);
                    }
                    else
                    {
                        type.Int32();
                    }
                });
            return metadata.GetOrAddBlob(blob);
        }

        void Field(InstructionEncoder il, ILOpCode operation, FieldDefinitionHandle field)
        {
            il.OpCode(operation);
            il.Token(field);
        }

        void Add(string methodName, InstructionEncoder il, BlobHandle signature, MethodAttributes attributes)
        {
            metadata.AddMethodDefinition(attributes, MethodImplAttributes.IL, metadata.GetOrAddString(methodName), signature,
                stream.AddMethodBody(il, maxStack: 8), MetadataTokens.ParameterHandle(1));
        }

        const MethodAttributes publicStatic = MethodAttributes.Public | MethodAttributes.Static;
        const MethodAttributes privateStatic = MethodAttributes.Private | MethodAttributes.Static;
        var selected = new InstructionEncoder(new BlobBuilder());
        selected.LoadConstantI4(shape == "write" ? 41 : 42);
        Field(selected, ILOpCode.Stsfld, state);
        if (shape == "revisit")
        {
            selected.Call(fetch);
            selected.OpCode(ILOpCode.Pop);
            selected.Call(bridgePublish);
        }

        if (shape == "external")
        {
            var maximum = new BlobBuilder();
            new BlobEncoder(maximum).MethodSignature().Parameters(2, result => result.Type().Int32(), parameters =>
            {
                parameters.AddParameter().Type().Int32();
                parameters.AddParameter().Type().Int32();
            });
            Field(selected, ILOpCode.Ldsfld, state);
            selected.LoadConstantI4(0);
            selected.Call(metadata.AddMemberReference(Type("System", "Math"), metadata.GetOrAddString("Max"),
                metadata.GetOrAddBlob(maximum)));
        }
        else
        {
            EntityHandle target = fetch;
            if (shape == "generic")
            {
                var arguments = new BlobBuilder();
                new BlobEncoder(arguments).MethodSpecificationSignature(1).AddArgument().Type(owner, isValueType: false);
                target = metadata.AddMethodSpecification(fetch, metadata.GetOrAddBlob(arguments));
                selected.OpCode(ILOpCode.Ldnull);
            }

            if (shape == "signature")
            {
                selected.OpCode(ILOpCode.Ldnull);
            }

            if (shape == "cycle")
            {
                selected.LoadConstantI4(1);
            }

            selected.Call(target);
            if (shape == "write")
            {
                Field(selected, ILOpCode.Ldsfld, state);
            }

            if (shape == "identity")
            {
                var equalSignature = new BlobBuilder();
                new BlobEncoder(equalSignature).MethodSignature().Parameters(2, result => result.Type().Boolean(), parameters =>
                {
                    parameters.AddParameter().Type().String();
                    parameters.AddParameter().Type().String();
                });
                selected.LoadString(metadata.GetOrAddUserString("PublicContext.Helper"));
                selected.Call(metadata.AddMemberReference(Type("System", "String"), metadata.GetOrAddString("op_Equality"),
                    metadata.GetOrAddBlob(equalSignature)));
                Field(selected, ILOpCode.Ldsfld, state);
                selected.OpCode(ILOpCode.Mul);
            }

            if (shape == "stateless")
            {
                Field(selected, ILOpCode.Ldsfld, state);
                selected.OpCode(ILOpCode.Add);
            }

            if (shape == "callback")
            {
                var invoke = new BlobBuilder();
                new BlobEncoder(invoke).MethodSignature(isInstanceMethod: true).Parameters(0,
                    result => result.Type().GenericTypeParameter(0), _ => { });
                selected.OpCode(ILOpCode.Callvirt);
                selected.Token(metadata.AddMemberReference(callbackType, metadata.GetOrAddString("Invoke"),
                    metadata.GetOrAddBlob(invoke)));
            }
        }

        selected.OpCode(ILOpCode.Ret);
        Add("Read", selected, Signature(), publicStatic);
        var reader = new InstructionEncoder(new BlobBuilder());
        Field(reader, ILOpCode.Ldsfld, state);
        reader.OpCode(ILOpCode.Ret);
        Add("ReadState", reader, Signature(), publicStatic);
        var unused = new InstructionEncoder(new BlobBuilder());
        unused.OpCode(ILOpCode.Ret);
        Add("Unused", unused, Signature(returnsVoid: true), privateStatic);
        var graph = new ControlFlowBuilder();
        var body = new InstructionEncoder(new BlobBuilder(), graph);
        if (shape is "identity" or "lookup")
        {
            var reflected = Type("System", "Type");
            var fromHandle = new BlobBuilder();
            new BlobEncoder(fromHandle).MethodSignature().Parameters(1,
                result => result.Type().Type(reflected, isValueType: false),
                parameters => parameters.AddParameter().Type().Type(Type("System", "RuntimeTypeHandle"), isValueType: true));
            body.OpCode(ILOpCode.Ldtoken);
            body.Token(helper);
            body.Call(metadata.AddMemberReference(reflected, metadata.GetOrAddString("GetTypeFromHandle"),
                metadata.GetOrAddBlob(fromHandle)));
            var property = new BlobBuilder();
            if (shape == "identity")
            {
                new BlobEncoder(property).MethodSignature(isInstanceMethod: true).Parameters(0,
                    result => result.Type().String(), _ => { });
                body.OpCode(ILOpCode.Callvirt);
                body.Token(metadata.AddMemberReference(reflected, metadata.GetOrAddString("get_FullName"),
                    metadata.GetOrAddBlob(property)));
            }
            else
            {
                var assembly = Type("System.Reflection", "Assembly");
                new BlobEncoder(property).MethodSignature(isInstanceMethod: true).Parameters(0,
                    result => result.Type().Type(assembly, isValueType: false), _ => { });
                body.OpCode(ILOpCode.Callvirt);
                body.Token(metadata.AddMemberReference(reflected, metadata.GetOrAddString("get_Assembly"),
                    metadata.GetOrAddBlob(property)));
                var lookup = new BlobBuilder();
                new BlobEncoder(lookup).MethodSignature(isInstanceMethod: true).Parameters(1,
                    result => result.Type().Type(reflected, isValueType: false), parameters => parameters.AddParameter().Type().String());
                body.LoadString(metadata.GetOrAddUserString("PublicContext.Owner"));
                body.OpCode(ILOpCode.Callvirt);
                body.Token(metadata.AddMemberReference(assembly, metadata.GetOrAddString("GetType"), metadata.GetOrAddBlob(lookup)));
                var fieldType = Type("System.Reflection", "FieldInfo");
                var fieldLookup = new BlobBuilder();
                new BlobEncoder(fieldLookup).MethodSignature(isInstanceMethod: true).Parameters(1,
                    result => result.Type().Type(fieldType, isValueType: false), parameters => parameters.AddParameter().Type().String());
                body.LoadString(metadata.GetOrAddUserString("State"));
                body.OpCode(ILOpCode.Callvirt);
                body.Token(metadata.AddMemberReference(reflected, metadata.GetOrAddString("GetField"), metadata.GetOrAddBlob(fieldLookup)));
                var getValue = new BlobBuilder();
                new BlobEncoder(getValue).MethodSignature(isInstanceMethod: true).Parameters(1,
                    result => result.Type().Object(), parameters => parameters.AddParameter().Type().Object());
                body.OpCode(ILOpCode.Ldnull);
                body.OpCode(ILOpCode.Callvirt);
                body.Token(metadata.AddMemberReference(fieldType, metadata.GetOrAddString("GetValue"), metadata.GetOrAddBlob(getValue)));
                body.OpCode(ILOpCode.Unbox_any);
                body.Token(Type("System", "Int32"));
            }
        }
        else if (shape == "callback")
        {
            var constructor = new BlobBuilder();
            new BlobEncoder(constructor).MethodSignature(isInstanceMethod: true).Parameters(2, result => result.Void(), parameters =>
            {
                parameters.AddParameter().Type().Object();
                parameters.AddParameter().Type().IntPtr();
            });
            body.OpCode(ILOpCode.Ldnull);
            body.OpCode(ILOpCode.Ldftn);
            body.Token(readState);
            body.OpCode(ILOpCode.Newobj);
            body.Token(metadata.AddMemberReference(callbackType, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(constructor)));
        }
        else if (shape == "cycle")
        {
            var direct = body.DefineLabel();
            body.LoadArgument(0);
            body.Branch(ILOpCode.Brfalse_s, direct);
            body.LoadArgument(0);
            body.LoadConstantI4(1);
            body.OpCode(ILOpCode.Sub);
            body.Call(otherFetch);
            body.OpCode(ILOpCode.Ret);
            body.MarkLabel(direct);
            Field(body, ILOpCode.Ldsfld, state);
        }
        else if (shape == "write")
        {
            Field(body, ILOpCode.Ldsfld, state);
            body.LoadConstantI4(1);
            body.OpCode(ILOpCode.Add);
            Field(body, ILOpCode.Stsfld, state);
        }
        else if (shape == "transitive")
        {
            body.Call(otherFetch);
        }
        else if (shape == "stateless" || shape == "external")
        {
            body.LoadConstantI4(0);
        }
        else
        {
            Field(body, ILOpCode.Ldsfld, shape == "cctor" ? cached : shape == "revisit" ? otherState : state);
        }

        body.OpCode(ILOpCode.Ret);
        Add("Fetch", body, Signature(returnsVoid: shape == "write", parameter: shape is "generic" or "signature" or "cycle",
            generic: shape == "generic", ownerParameter: shape == "signature", callback: shape == "callback",
            text: shape == "identity"), publicStatic);
        if (shape == "generic")
        {
            metadata.AddGenericParameter(fetch, GenericParameterAttributes.None, metadata.GetOrAddString("T"), 0);
        }

        var initializer = new InstructionEncoder(new BlobBuilder());
        if (shape == "cctor")
        {
            Field(initializer, ILOpCode.Ldsfld, state);
            Field(initializer, ILOpCode.Stsfld, cached);
        }

        initializer.OpCode(ILOpCode.Ret);
        Add(shape == "cctor" ? ".cctor" : "Unused", initializer, Signature(returnsVoid: true), shape == "cctor"
            ? privateStatic | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName : privateStatic);
        var transit = new InstructionEncoder(new BlobBuilder());
        if (shape == "cycle")
        {
            transit.LoadArgument(0);
            transit.Call(fetch);
        }
        else
        {
            Field(transit, ILOpCode.Ldsfld, state);
        }

        transit.OpCode(ILOpCode.Ret);
        Add("Fetch", transit, Signature(parameter: shape == "cycle"), publicStatic);
        var bridge = new InstructionEncoder(new BlobBuilder());
        Field(bridge, ILOpCode.Ldsfld, state);
        Field(bridge, ILOpCode.Stsfld, otherState);
        bridge.OpCode(ILOpCode.Ret);
        Add("Publish", bridge, Signature(returnsVoid: true), publicStatic);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }

    /// <summary>
    /// Produces a fixed scenario that observes the selected return in each independent comparison runtime.
    /// </summary>
    /// <returns>The complete parameterless scenario.</returns>
    public static string Scenario() => ".method int32 Scenario() {\ncall Copy\nret\n}";
}
