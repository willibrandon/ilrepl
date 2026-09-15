using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits unrelated leading metadata rows so copied member tokens cannot accidentally retain their original source values.
/// </summary>
public static class MemberTokenFixture
{
    /// <summary>
    /// Explains why copied members cannot preserve their original numeric metadata tokens.
    /// </summary>
    public const string Problem = "member token inspection cannot reproduce the original metadata tokens";

    /// <summary>
    /// Covers member families and representative direct, indirect and unproven receiver paths.
    /// </summary>
    public static IReadOnlyList<(string Kind, string Dispatch)> Cases { get; } =
    [
        ("Type", "direct"), ("Method", "direct"), ("Constructor", "direct"), ("Field", "direct"),
        ("Property", "direct"), ("Event", "direct"), ("Parameter", "direct"),
        ("Type", "invoke"), ("Field", "property"), ("Method", "delegate"), ("Type", "named"),
        ("Property", "pointer"), ("Type", "invoke-member"), ("Method", "invoker"), ("Method", "unknown"),
        ("GenericParameter", "direct"), ("GenericTypeParameter", "direct"),
    ];

    /// <summary>
    /// Known external members, standalone method tokens, user lookalikes and ordinary member names remain supported.
    /// </summary>
    public static IReadOnlyList<(string Kind, string Dispatch)> SupportedCases { get; } =
    [
        ("Type", "external"), ("Method", "external"), ("Constructor", "external"), ("Field", "external"),
        ("Property", "external"), ("Event", "external"), ("Parameter", "external"),
        ("Type", "external-named"), ("Field", "external-property"), ("Type", "external-sibling"),
        ("Type", "token"), ("Parameter", "token"), ("Type", "lookalike"), ("Method", "name"),
        ("Array", "direct"), ("ByRef", "direct"), ("Constructed", "direct"),
    ];

    /// <summary>
    /// Identifies the independently prescribed source row for each owner member.
    /// </summary>
    /// <param name="kind">The member family.</param>
    /// <returns>The original numeric token after eight unrelated leading owner definitions.</returns>
    public static int SourceToken(string kind) => kind switch
    {
        "Type" => 0x0200000a, "Field" => 0x04000009, "Method" => 0x06000022, "Constructor" => 0x06000026,
        "Property" => 0x17000009, "Event" => 0x14000009, "Parameter" => 0x08000019,
        "GenericParameter" or "GenericTypeParameter" => 0x2a000009,
        _ => throw new ArgumentException("unknown member family", nameof(kind)),
    };

    /// <summary>
    /// Creates a real source PE whose owner observes its original member tokens and returns forty-two.
    /// </summary>
    /// <param name="kind">The reflected member family.</param>
    /// <param name="dispatch">The getter, reflection, delegate or supported external route.</param>
    /// <returns>The independently executable source image.</returns>
    public static byte[] Create(string kind, string dispatch)
    {
        var metadata = new MetadataBuilder();
        var name = "MemberTokenSource" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(7, 8, 9, 10), default, default, 0, AssemblyHashAlgorithm.None);
        var references = new Dictionary<Assembly, AssemblyReferenceHandle>();
        var types = new Dictionary<Type, TypeReferenceHandle>();
        TypeReferenceHandle TypeReference(Type type)
        {
            if (types.TryGetValue(type, out var handle)) return handle;
            if (!references.TryGetValue(type.Assembly, out var reference))
            {
                var identity = type.Assembly.GetName();
                reference = metadata.AddAssemblyReference(metadata.GetOrAddString(identity.Name!), identity.Version!, default,
                    metadata.GetOrAddBlob(identity.GetPublicKeyToken()!), 0, default);
                references.Add(type.Assembly, reference);
            }

            handle = metadata.AddTypeReference(type.IsNested ? TypeReference(type.DeclaringType!) : reference,
                metadata.GetOrAddString(type.Namespace ?? ""), metadata.GetOrAddString(type.Name));
            types.Add(type, handle);
            return handle;
        }

        void EncodeType(SignatureTypeEncoder encoder, Type type)
        {
            if (type == typeof(bool)) encoder.Boolean();
            else if (type == typeof(byte)) encoder.Byte();
            else if (type == typeof(int)) encoder.Int32();
            else if (type == typeof(string)) encoder.String();
            else if (type == typeof(object)) encoder.Object();
            else if (type == typeof(nint)) encoder.IntPtr();
            else if (type.IsArray) EncodeType(encoder.SZArray(), type.GetElementType()!);
            else if (type.IsGenericParameter)
            {
                if (type.DeclaringMethod is null) encoder.GenericTypeParameter(type.GenericParameterPosition);
                else encoder.GenericMethodTypeParameter(type.GenericParameterPosition);
            }
            else if (type.IsGenericType)
            {
                var arguments = type.GetGenericArguments();
                var parameters = encoder.GenericInstantiation(TypeReference(type.GetGenericTypeDefinition()), arguments.Length,
                    type.IsValueType);
                foreach (var argument in arguments) EncodeType(parameters.AddArgument(), argument);
            }
            else encoder.Type(TypeReference(type), type.IsValueType);
        }

        EntityHandle MethodReference(MethodBase method)
        {
            var definition = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : method;
            var declaring = method.DeclaringType!;
            if (declaring.IsConstructedGenericType)
                definition = declaring.GetGenericTypeDefinition().GetMethods().Cast<MethodBase>()
                    .Concat(declaring.GetGenericTypeDefinition().GetConstructors())
                    .Single(candidate => candidate.MetadataToken == definition.MetadataToken);
            var signature = new BlobBuilder();
            var parameters = definition.GetParameters();
            new BlobEncoder(signature).MethodSignature(genericParameterCount: definition.IsGenericMethod
                ? definition.GetGenericArguments().Length : 0, isInstanceMethod: !definition.IsStatic).Parameters(parameters.Length,
                result =>
                {
                    if (definition is not MethodInfo info || info.ReturnType == typeof(void)) result.Void();
                    else EncodeType(result.Type(), info.ReturnType);
                }, arguments =>
                {
                    foreach (var parameter in parameters) EncodeType(arguments.AddParameter().Type(), parameter.ParameterType);
                });
            var parent = declaring.IsConstructedGenericType ? SignatureType(declaring) : TypeReference(declaring);
            var member = metadata.AddMemberReference(parent, metadata.GetOrAddString(definition.Name), metadata.GetOrAddBlob(signature));
            if (method is not MethodInfo { IsGenericMethod: true } closed) return member;
            var specification = new BlobBuilder();
            var encoded = new BlobEncoder(specification).MethodSpecificationSignature(closed.GetGenericArguments().Length);
            foreach (var argument in closed.GetGenericArguments()) EncodeType(encoded.AddArgument(), argument);
            return metadata.AddMethodSpecification(member, metadata.GetOrAddBlob(specification));
        }

        EntityHandle SignatureType(Type type)
        {
            if (!type.IsArray && !type.IsGenericType) return TypeReference(type);
            var signature = new BlobBuilder();
            EncodeType(new BlobEncoder(signature).TypeSpecificationSignature(), type);
            return metadata.AddTypeSpecification(metadata.GetOrAddBlob(signature));
        }

        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        for (var index = 0; index < 8; index++)
            metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("MemberTokens"),
                metadata.GetOrAddString("Padding" + index), TypeReference(typeof(object)),
                MetadataTokens.FieldDefinitionHandle(index + 1), MetadataTokens.MethodDefinitionHandle(index * 4 + 1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("MemberTokens"),
            metadata.GetOrAddString("Owner"), TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(9),
            MetadataTokens.MethodDefinitionHandle(33));
        TypeDefinitionHandle genericOwner = default;
        if (kind == "GenericTypeParameter")
        {
            var padding = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("MemberTokens"),
                metadata.GetOrAddString("GenericPadding`8"), TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(10),
                MetadataTokens.MethodDefinitionHandle(39));
            for (var index = 0; index < 8; index++)
                metadata.AddGenericParameter(padding, 0, metadata.GetOrAddString("T" + index), index);
            genericOwner = metadata.AddTypeDefinition(TypeAttributes.NestedPrivate, default,
                metadata.GetOrAddString("Hidden`1"), TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(10),
                MetadataTokens.MethodDefinitionHandle(39));
            metadata.AddNestedType(genericOwner, owner);
            metadata.AddGenericParameter(genericOwner, 0, metadata.GetOrAddString("T"), 0);
        }
        var bodies = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(bodies);
        var parameterRow = 1;
        MethodDefinitionHandle Method(string methodName, Type result, Type[] parameters, InstructionEncoder instructions,
            bool constructor = false)
        {
            var signature = new BlobBuilder();
            var generic = methodName == "Probe" && kind == "GenericParameter";
            new BlobEncoder(signature).MethodSignature(genericParameterCount: generic ? 1 : 0,
                isInstanceMethod: constructor).Parameters(parameters.Length,
                returns =>
                {
                    if (result == typeof(void)) returns.Void();
                    else EncodeType(returns.Type(), result);
                }, arguments =>
                {
                    foreach (var parameter in parameters) EncodeType(arguments.AddParameter().Type(), parameter);
                });
            var first = MetadataTokens.ParameterHandle(parameterRow);
            for (var index = 0; index < parameters.Length; index++)
            {
                metadata.AddParameter(0, metadata.GetOrAddString("value" + index), index + 1);
                parameterRow++;
            }
            var attributes = MethodAttributes.Public | (constructor ? MethodAttributes.SpecialName | MethodAttributes.RTSpecialName
                : MethodAttributes.Static);
            if (methodName.StartsWith("get_", StringComparison.Ordinal) || methodName.StartsWith("add_", StringComparison.Ordinal)
                || methodName.StartsWith("remove_", StringComparison.Ordinal)) attributes |= MethodAttributes.SpecialName;
            var method = metadata.AddMethodDefinition(attributes, MethodImplAttributes.IL, metadata.GetOrAddString(methodName),
                metadata.GetOrAddBlob(signature), encoder.AddMethodBody(instructions, maxStack: 8), first);
            if (generic) metadata.AddGenericParameter(method, 0, metadata.GetOrAddString("T"), 0);
            return method;
        }

        InstructionEncoder Constant()
        {
            var instructions = new InstructionEncoder(new BlobBuilder());
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Ret);
            return instructions;
        }

        void Members(TypeDefinitionHandle type)
        {
            byte[] fieldSignature = [6, 8];
            metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, metadata.GetOrAddString("Data"),
                metadata.GetOrAddBlob(fieldSignature));
            Method("Probe", typeof(int), [typeof(int)], Constant());
            var getter = Method("get_Value", typeof(int), [], Constant());
            var empty = new InstructionEncoder(new BlobBuilder());
            empty.OpCode(ILOpCode.Ret);
            var adder = Method("add_Changed", typeof(void), [typeof(Action)], empty);
            var remover = Method("remove_Changed", typeof(void), [typeof(Action)], empty);
            byte[] propertySignature = [8, 0, 8];
            var property = metadata.AddProperty(0, metadata.GetOrAddString("Value"), metadata.GetOrAddBlob(propertySignature));
            metadata.AddPropertyMap(type, property);
            metadata.AddMethodSemantics(property, MethodSemanticsAttributes.Getter, getter);
            var changed = metadata.AddEvent(0, metadata.GetOrAddString("Changed"), TypeReference(typeof(Action)));
            metadata.AddEventMap(type, changed);
            metadata.AddMethodSemantics(changed, MethodSemanticsAttributes.Adder, adder);
            metadata.AddMethodSemantics(changed, MethodSemanticsAttributes.Remover, remover);
        }
        for (var index = 0; index < 8; index++) Members(MetadataTokens.TypeDefinitionHandle(index + 2));
        var instructions = new InstructionEncoder(new BlobBuilder());
        void Call(MethodBase method)
        {
            instructions.OpCode(method.IsStatic || method is ConstructorInfo ? ILOpCode.Call : ILOpCode.Callvirt);
            instructions.Token(MethodReference(method));
        }

        void LoadType(EntityHandle type)
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(type);
            Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        }

        var external = dispatch.StartsWith("external", StringComparison.Ordinal);
        void LoadReceiver()
        {
            if (kind == "GenericTypeParameter")
            {
                LoadType(genericOwner);
                Call(typeof(Type).GetMethod(nameof(Type.GetGenericArguments))!);
                instructions.LoadConstantI4(0);
                instructions.OpCode(ILOpCode.Ldelem_ref);
                return;
            }
            if (kind is "Array" or "ByRef" or "Constructed")
            {
                var signature = new BlobBuilder();
                if (kind == "ByRef") signature.WriteByte(0x10);
                var encoded = new BlobEncoder(signature).TypeSpecificationSignature();
                if (kind == "Array") encoded.SZArray().Type(owner, false);
                else if (kind == "Constructed")
                    encoded.GenericInstantiation(TypeReference(typeof(List<>)), 1, false).AddArgument().Type(owner, false);
                else encoded.Type(owner, false);
                LoadType(metadata.AddTypeSpecification(metadata.GetOrAddBlob(signature)));
                return;
            }
            var externalType = kind switch
            {
                "Field" => typeof(int), "Method" or "Constructor" or "Parameter" => typeof(object),
                "Event" => typeof(AppDomain), _ => typeof(string),
            };
            LoadType(dispatch == "external-sibling" ? MetadataTokens.TypeDefinitionHandle(2)
                : external ? TypeReference(externalType) : owner);
            if (kind == "Type") return;
            if (kind == "Constructor")
            {
                instructions.LoadConstantI4(0);
                instructions.OpCode(ILOpCode.Newarr);
                instructions.Token(TypeReference(typeof(Type)));
                Call(typeof(Type).GetMethod(nameof(Type.GetConstructor), [typeof(Type[])])!);
                return;
            }
            var memberName = kind switch
            {
                "Field" => external ? "MaxValue" : "Data", "Property" => external ? "Length" : "Value",
                "Event" => external ? "AssemblyLoad" : "Changed", _ => external ? "ReferenceEquals" : "Probe",
            };
            instructions.LoadString(metadata.GetOrAddUserString(dispatch == "unknown" ? " Probe " : memberName));
            if (dispatch == "unknown") Call(typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!);
            var lookup = kind switch { "Field" => nameof(Type.GetField), "Property" => nameof(Type.GetProperty),
                "Event" => nameof(Type.GetEvent), _ => nameof(Type.GetMethod) };
            Call(typeof(Type).GetMethod(lookup, [typeof(string)])!);
            if (kind is not ("Parameter" or "GenericParameter")) return;
            Call(typeof(MethodBase).GetMethod(kind == "Parameter" ? nameof(MethodBase.GetParameters)
                : nameof(MethodBase.GetGenericArguments))!);
            instructions.LoadConstantI4(0);
            instructions.OpCode(ILOpCode.Ldelem_ref);
        }

        var receiverType = kind == "Parameter" ? typeof(ParameterInfo) : typeof(MemberInfo);
        var inspection = receiverType.GetProperty(nameof(MemberInfo.MetadataToken))!.GetMethod!;
        void LoadInspection()
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(inspection));
            Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
            instructions.OpCode(ILOpCode.Castclass);
            instructions.Token(TypeReference(typeof(MethodInfo)));
        }

        var route = external && dispatch != "external-sibling" ? dispatch.Replace("external-", "", StringComparison.Ordinal) : dispatch;
        if (route == "token")
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(inspection));
            instructions.OpCode(ILOpCode.Pop);
            instructions.LoadConstantI4(42);
        }
        else if (route == "lookalike") instructions.Call(MetadataTokens.MethodDefinitionHandle(39));
        else
        {
            if (route == "invoke")
            {
                LoadInspection();
                LoadReceiver();
                instructions.OpCode(ILOpCode.Ldnull);
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                instructions.OpCode(ILOpCode.Unbox_any);
                instructions.Token(TypeReference(typeof(int)));
            }
            else if (route == "property")
            {
                LoadType(TypeReference(receiverType));
                instructions.LoadString(metadata.GetOrAddUserString(nameof(MemberInfo.MetadataToken)));
                Call(typeof(Type).GetMethod(nameof(Type.GetProperty), [typeof(string)])!);
                LoadReceiver();
                instructions.OpCode(ILOpCode.Ldnull);
                Call(typeof(PropertyInfo).GetMethod(nameof(PropertyInfo.GetValue), [typeof(object), typeof(object[])])!);
                instructions.OpCode(ILOpCode.Unbox_any);
                instructions.Token(TypeReference(typeof(int)));
            }
            else if (route is "delegate" or "named" or "pointer")
            {
                if (route == "delegate") LoadInspection();
                if (route != "pointer") LoadType(SignatureType(typeof(Func<int>)));
                LoadReceiver();
                if (route == "pointer")
                {
                    instructions.OpCode(ILOpCode.Dup);
                    instructions.OpCode(ILOpCode.Ldvirtftn);
                    instructions.Token(MethodReference(inspection));
                    instructions.OpCode(ILOpCode.Newobj);
                    instructions.Token(MethodReference(typeof(Func<int>).GetConstructors().Single()));
                }
                else if (route == "named")
                {
                    instructions.LoadString(metadata.GetOrAddUserString("get_MetadataToken"));
                    Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(object), typeof(string)])!);
                }
                else Call(typeof(MethodInfo).GetMethod(nameof(MethodInfo.CreateDelegate), [typeof(Type), typeof(object)])!);
                instructions.OpCode(ILOpCode.Castclass);
                instructions.Token(SignatureType(typeof(Func<int>)));
                Call(typeof(Func<int>).GetMethod(nameof(Func<int>.Invoke))!);
            }
            else if (route == "invoke-member")
            {
                LoadType(TypeReference(receiverType));
                instructions.LoadString(metadata.GetOrAddUserString(nameof(MemberInfo.MetadataToken)));
                instructions.LoadConstantI4((int)(BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty));
                instructions.OpCode(ILOpCode.Ldnull);
                LoadReceiver();
                instructions.OpCode(ILOpCode.Ldnull);
                Call(typeof(Type).GetMethod(nameof(Type.InvokeMember),
                    [typeof(string), typeof(BindingFlags), typeof(Binder), typeof(object), typeof(object[])])!);
                instructions.OpCode(ILOpCode.Unbox_any);
                instructions.Token(TypeReference(typeof(int)));
            }
            else if (route == "invoker")
            {
                LoadInspection();
                Call(typeof(MethodInvoker).GetMethod(nameof(MethodInvoker.Create))!);
                LoadReceiver();
                Call(typeof(MethodInvoker).GetMethod(nameof(MethodInvoker.Invoke), [typeof(object)])!);
                instructions.OpCode(ILOpCode.Unbox_any);
                instructions.Token(TypeReference(typeof(int)));
            }
            else
            {
                LoadReceiver();
                Call(route == "name" ? typeof(MemberInfo).GetProperty(nameof(MemberInfo.Name))!.GetMethod! : inspection);
            }
            if (route == "name")
            {
                instructions.LoadString(metadata.GetOrAddUserString("Probe"));
                Call(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
            }
            else if (kind is "Array" or "ByRef" or "GenericParameter" or "GenericTypeParameter")
            {
                instructions.LoadConstantI4(kind is "GenericParameter" or "GenericTypeParameter" ? SourceToken(kind) : 0x02000000);
                instructions.OpCode(ILOpCode.Ceq);
                LoadReceiver();
                Call(inspection);
                instructions.LoadConstantI4(kind == "ByRef" ? SourceToken("Type") : 0);
                instructions.OpCode(ILOpCode.Ceq);
                instructions.OpCode(ILOpCode.Or);
            }
            else if (kind == "Constructed")
            {
                LoadType(TypeReference(typeof(List<>)));
                Call(inspection);
                instructions.OpCode(ILOpCode.Ceq);
            }
            else
            {
                instructions.LoadConstantI4(dispatch == "external-sibling" ? 0x02000002 : external ? 0 : SourceToken(kind));
                instructions.OpCode(external && dispatch != "external-sibling" ? ILOpCode.Cgt : ILOpCode.Ceq);
            }
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Mul);
        }
        instructions.OpCode(ILOpCode.Ret);
        Method("Read", typeof(int), [], instructions);
        Members(owner);
        instructions = new InstructionEncoder(new BlobBuilder());
        instructions.LoadArgument(0);
        Call(typeof(object).GetConstructor(Type.EmptyTypes)!);
        instructions.OpCode(ILOpCode.Ret);
        Method(".ctor", typeof(void), [], instructions, constructor: true);
        if (dispatch == "lookalike") Method("get_MetadataToken", typeof(int), [], Constant());
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
