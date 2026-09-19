using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits a unique source identity and real assembly inspection whose result changes when its owner is generated afresh.
/// </summary>
public static class AssemblyIdentityFixture
{
    /// <summary>
    /// The expected preflight explanation for source assembly identity that a generated context cannot preserve.
    /// </summary>
    public const string Problem = "assembly identity inspection cannot reproduce the original assembly identity";

    /// <summary>
    /// Bounded API, dispatch, and receiver variants exercise each identity observation without multiplying equivalent cases.
    /// </summary>
    public static IReadOnlyList<(string Api, string Dispatch, string Receiver)> Cases { get; } =
    [
        ("GetName", "direct", "executing"),
        ("GetName(bool)", "direct", "executing"),
        ("FullName", "direct", "executing"),
        ("ToString", "direct", "executing"),
        ("ToString", "object dispatch", "executing"),
        ("ToString", "object invoke", "executing"),
        ("ToString", "object method delegate", "executing"),
        ("ToString", "open method delegate", "executing"),
        ("GetName", "invoke", "executing"),
        ("FullName", "property", "executing"),
        ("GetName", "named delegate", "executing"),
        ("ToString", "runtime named delegate", "executing"),
        ("GetName", "direct", "owner"),
        ("FullName", "direct", "module"),
    ];

    /// <summary>
    /// Supplies metadata-only and ordinary user/type/member operations that must retain supported behavior.
    /// </summary>
    public static IReadOnlyList<(string Api, string Dispatch)> SupportedCases { get; } =
    [
        ("GetName", "token"),
        ("GetName(bool)", "token"),
        ("FullName", "token"),
        ("ToString", "token"),
        ("GetName", "lookalike"),
        ("FullName", "lookalike"),
        ("ToString", "lookalike"),
        ("FullName", "type metadata"),
        ("Name", "member metadata"),
        ("ToString", "ordinary tostring"),
        ("ToString", "ordinary delegate"),
    ];

    /// <summary>
    /// Creates an actual PE whose selected identity or supported metadata operation returns exactly 42.
    /// </summary>
    /// <param name="api">The Assembly API, property, or ordinary metadata member.</param>
    /// <param name="dispatch">The direct, reflection, delegate, token, or supported user operation.</param>
    /// <param name="receiver">The executing assembly, owner Type.Assembly, or owner Module.Assembly flow.</param>
    /// <returns>The complete independently executable image.</returns>
    public static byte[] Create(string api, string dispatch, string receiver = "executing")
    {
        var metadata = new MetadataBuilder();
        var name = "IdentitySource" + Guid.NewGuid().ToString("N");
        var identity = new AssemblyName { Name = name, Version = new Version(7, 8, 9, 10), CultureName = "" };
        identity.SetPublicKeyToken([]);
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), identity.Version!, default, default, 0, AssemblyHashAlgorithm.None);
        var references = new Dictionary<Assembly, AssemblyReferenceHandle>();
        var types = new Dictionary<Type, TypeReferenceHandle>();
        TypeReferenceHandle TypeReference(Type type)
        {
            if (types.TryGetValue(type, out var handle))
            {
                return handle;
            }

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
            if (type == typeof(bool))
            {
                encoder.Boolean();
            }
            else if (type == typeof(int))
            {
                encoder.Int32();
            }
            else if (type == typeof(string))
            {
                encoder.String();
            }
            else if (type == typeof(object))
            {
                encoder.Object();
            }
            else if (type == typeof(nint))
            {
                encoder.IntPtr();
            }
            else if (type.IsArray)
            {
                EncodeType(encoder.SZArray(), type.GetElementType()!);
            }
            else if (type.IsGenericParameter)
            {
                if (type.DeclaringMethod is null)
                {
                    encoder.GenericTypeParameter(type.GenericParameterPosition);
                }
                else
                {
                    encoder.GenericMethodTypeParameter(type.GenericParameterPosition);
                }
            }
            else if (type.IsGenericType)
            {
                var arguments = type.GetGenericArguments();
                var parameters = encoder.GenericInstantiation(TypeReference(type.GetGenericTypeDefinition()), arguments.Length,
                    type.IsValueType);
                foreach (var argument in arguments)
                {
                    EncodeType(parameters.AddArgument(), argument);
                }
            }
            else
            {
                encoder.Type(TypeReference(type), type.IsValueType);
            }
        }

        EntityHandle MethodReference(MethodBase method)
        {
            var definition = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : method;
            var declaring = method.DeclaringType!;
            if (declaring.IsConstructedGenericType)
            {
                definition = declaring.GetGenericTypeDefinition().GetMethods().Cast<MethodBase>()
                    .Concat(declaring.GetGenericTypeDefinition().GetConstructors())
                    .Single(candidate => candidate.MetadataToken == definition.MetadataToken);
            }

            var signature = new BlobBuilder();
            var parameters = definition.GetParameters();
            new BlobEncoder(signature).MethodSignature(genericParameterCount: definition.IsGenericMethod
                ? definition.GetGenericArguments().Length : 0, isInstanceMethod: !definition.IsStatic).Parameters(parameters.Length,
                result =>
                {
                    if (definition is not MethodInfo info || info.ReturnType == typeof(void))
                    {
                        result.Void();
                    }
                    else
                    {
                        EncodeType(result.Type(), info.ReturnType);
                    }
                }, arguments =>
                {
                    foreach (var parameter in parameters)
                    {
                        EncodeType(arguments.AddParameter().Type(), parameter.ParameterType);
                    }
                });
            var parent = declaring.IsConstructedGenericType ? SignatureType(declaring) : TypeReference(declaring);
            var member = metadata.AddMemberReference(parent, metadata.GetOrAddString(definition.Name), metadata.GetOrAddBlob(signature));
            if (method is not MethodInfo { IsGenericMethod: true } closed)
            {
                return member;
            }

            var specification = new BlobBuilder();
            var encoded = new BlobEncoder(specification).MethodSpecificationSignature(closed.GetGenericArguments().Length);
            foreach (var argument in closed.GetGenericArguments())
            {
                EncodeType(encoded.AddArgument(), argument);
            }

            return metadata.AddMethodSpecification(member, metadata.GetOrAddBlob(specification));
        }

        EntityHandle SignatureType(Type type)
        {
            if (!type.IsArray && !type.IsGenericType)
            {
                return TypeReference(type);
            }

            var signature = new BlobBuilder();
            EncodeType(new BlobEncoder(signature).TypeSpecificationSignature(), type);
            return metadata.AddTypeSpecification(metadata.GetOrAddBlob(signature));
        }

        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("AssemblyIdentity"),
            metadata.GetOrAddString("Owner"), TypeReference(typeof(object)),
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var instructions = new InstructionEncoder(new BlobBuilder());
        var inspection = api switch
        {
            "GetName" => typeof(Assembly).GetMethod(nameof(Assembly.GetName), Type.EmptyTypes)!,
            "GetName(bool)" => typeof(Assembly).GetMethod(nameof(Assembly.GetName), [typeof(bool)])!,
            "FullName" => typeof(Assembly).GetProperty(nameof(Assembly.FullName))!.GetMethod!,
            "ToString" => (dispatch.StartsWith("object", StringComparison.Ordinal)
                || dispatch is "open method delegate" or "ordinary delegate" ? typeof(object) : typeof(Assembly))
                .GetMethod(nameof(Assembly.ToString))!,
            _ => typeof(MemberInfo).GetProperty(nameof(MemberInfo.Name))!.GetMethod!,
        };
        void Call(MethodBase method)
        {
            instructions.OpCode(method.IsStatic ? ILOpCode.Call : ILOpCode.Callvirt);
            instructions.Token(MethodReference(method));
        }

        void LoadType(EntityHandle type)
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(type);
            Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        }

        void LoadAssembly()
        {
            if (receiver == "executing")
            {
                Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
            }
            else
            {
                LoadType(owner);
                if (receiver == "module")
                {
                    Call(typeof(Type).GetProperty(nameof(Type.Module))!.GetMethod!);
                    Call(typeof(Module).GetProperty(nameof(Module.Assembly))!.GetMethod!);
                }
                else
                {
                    Call(typeof(Type).GetProperty(nameof(Type.Assembly))!.GetMethod!);
                }
            }
        }

        var expected = dispatch == "ordinary delegate" ? "System.Object" : api == "GetName" ? name : identity.FullName;
        if (dispatch == "token")
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(inspection));
            instructions.OpCode(ILOpCode.Pop);
            instructions.LoadConstantI4(42);
        }
        else if (dispatch == "lookalike")
        {
            instructions.Call(MetadataTokens.MethodDefinitionHandle(2));
        }
        else
        {
            if (dispatch == "type metadata")
            {
                LoadType(TypeReference(typeof(int)));
                Call(typeof(Type).GetProperty(nameof(Type.FullName))!.GetMethod!);
                expected = "System.Int32";
            }
            else if (dispatch == "member metadata")
            {
                LoadType(owner);
                instructions.LoadString(metadata.GetOrAddUserString("Read"));
                Call(typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string)])!);
                Call(typeof(MemberInfo).GetProperty(nameof(MemberInfo.Name))!.GetMethod!);
                expected = "Read";
            }
            else if (dispatch == "ordinary tostring")
            {
                instructions.OpCode(ILOpCode.Newobj);
                instructions.Token(MethodReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
                Call(typeof(object).GetMethod(nameof(ToString))!);
                expected = "System.Object";
            }
            else
            {
                if (dispatch is "invoke" or "object invoke" or "object method delegate" or "open method delegate" or "ordinary delegate")
                {
                    LoadType(TypeReference(dispatch == "invoke" ? typeof(Assembly) : typeof(object)));
                    instructions.LoadString(metadata.GetOrAddUserString(inspection.Name));
                    instructions.LoadConstantI4(0);
                    instructions.OpCode(ILOpCode.Newarr);
                    instructions.Token(TypeReference(typeof(Type)));
                    Call(typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string), typeof(Type[])])!);
                    if (dispatch == "open method delegate")
                    {
                        var delegateType = typeof(Func<object, string>);
                        LoadType(SignatureType(delegateType));
                        Call(typeof(MethodInfo).GetMethod(nameof(MethodInfo.CreateDelegate), [typeof(Type)])!);
                        instructions.OpCode(ILOpCode.Castclass);
                        instructions.Token(SignatureType(delegateType));
                        LoadAssembly();
                        Call(delegateType.GetMethod(nameof(Func<object, string>.Invoke))!);
                    }
                    else if (dispatch is "object method delegate" or "ordinary delegate")
                    {
                        LoadType(SignatureType(typeof(Func<string>)));
                        if (dispatch == "ordinary delegate")
                        {
                            instructions.OpCode(ILOpCode.Newobj);
                            instructions.Token(MethodReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
                        }
                        else
                        {
                            LoadAssembly();
                        }

                        Call(typeof(MethodInfo).GetMethod(nameof(MethodInfo.CreateDelegate), [typeof(Type), typeof(object)])!);
                        instructions.OpCode(ILOpCode.Ldnull);
                        Call(typeof(Delegate).GetMethod(nameof(Delegate.DynamicInvoke))!);
                    }
                    else
                    {
                        LoadAssembly();
                        instructions.OpCode(ILOpCode.Ldnull);
                        Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                    }

                    instructions.OpCode(ILOpCode.Castclass);
                    instructions.Token(TypeReference(api == "ToString" ? typeof(string) : typeof(AssemblyName)));
                }
                else if (dispatch == "property")
                {
                    LoadType(TypeReference(typeof(Assembly)));
                    instructions.LoadString(metadata.GetOrAddUserString(nameof(Assembly.FullName)));
                    Call(typeof(Type).GetMethod(nameof(Type.GetProperty), [typeof(string)])!);
                    LoadAssembly();
                    Call(typeof(PropertyInfo).GetMethod(nameof(PropertyInfo.GetValue), [typeof(object)])!);
                    instructions.OpCode(ILOpCode.Castclass);
                    instructions.Token(TypeReference(typeof(string)));
                }
                else if (dispatch is "named delegate" or "runtime named delegate")
                {
                    LoadType(SignatureType(api == "ToString" ? typeof(Func<string>) : typeof(Func<AssemblyName>)));
                    LoadAssembly();
                    if (dispatch == "runtime named delegate")
                    {
                        instructions.LoadArgument(0);
                    }
                    else
                    {
                        instructions.LoadString(metadata.GetOrAddUserString(nameof(Assembly.GetName)));
                    }

                    Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(object), typeof(string)])!);
                    instructions.OpCode(ILOpCode.Ldnull);
                    Call(typeof(Delegate).GetMethod(nameof(Delegate.DynamicInvoke))!);
                    instructions.OpCode(ILOpCode.Castclass);
                    instructions.Token(TypeReference(api == "ToString" ? typeof(string) : typeof(AssemblyName)));
                }
                else
                {
                    LoadAssembly();
                    if (api == "GetName(bool)")
                    {
                        instructions.LoadConstantI4(1);
                    }

                    Call(inspection);
                }

                if (api.StartsWith("GetName", StringComparison.Ordinal))
                {
                    Call(typeof(AssemblyName).GetProperty(api == "GetName" ? nameof(AssemblyName.Name) : nameof(AssemblyName.FullName))!
                        .GetMethod!);
                }
            }

            instructions.LoadString(metadata.GetOrAddUserString(expected));
            Call(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Mul);
        }

        instructions.OpCode(ILOpCode.Ret);
        var bodies = new BlobBuilder();
        var bodyEncoder = new MethodBodyStreamEncoder(bodies);
        var offset = bodyEncoder.AddMethodBody(instructions, maxStack: 5);
        byte[] signature = dispatch == "runtime named delegate" ? [0, 1, 8, 14] : [0, 0, 8];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(signature), offset, MetadataTokens.ParameterHandle(1));
        if (dispatch == "lookalike")
        {
            var helper = new InstructionEncoder(new BlobBuilder());
            helper.LoadConstantI4(42);
            helper.OpCode(ILOpCode.Ret);
            var helperOffset = bodyEncoder.AddMethodBody(helper);
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString(inspection.Name), metadata.GetOrAddBlob(signature), helperOffset,
                MetadataTokens.ParameterHandle(1));
        }

        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
