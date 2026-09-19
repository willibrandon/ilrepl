using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits genuine module-global methods and RVA fields whose discovery executes against the original global member table.
/// </summary>
public static class ModuleGlobalFixture
{
    /// <summary>
    /// Explains why generated modules cannot reproduce source global members.
    /// </summary>
    public const string Problem = "module global inspection cannot reproduce the original global members";

    /// <summary>
    /// Covers all nine public lookup overloads and representative reflected and delegate dispatch.
    /// </summary>
    public static IReadOnlyList<(string Api, string Dispatch)> Cases { get; } =
    [
        ("GetMethod", "direct"), ("GetMethod(types)", "direct"), ("GetMethod(binding)", "direct"),
        ("GetMethods", "direct"), ("GetMethods(flags)", "direct"),
        ("GetField", "direct"), ("GetField(flags)", "direct"),
        ("GetFields", "direct"), ("GetFields(flags)", "direct"),
        ("GetMethod", "invoke"), ("GetFields", "invoke"),
        ("GetMethods", "delegate"), ("GetFields", "delegate"), ("GetMethods", "pointer"),
    ];

    /// <summary>
    /// Standalone BCL method tokens and user-defined names do not inspect module global tables.
    /// </summary>
    public static IReadOnlyList<(string Api, string Dispatch)> SupportedCases { get; } =
    [
        ("GetMethod", "token"), ("GetMethods", "token"), ("GetField", "token"), ("GetFields", "token"),
        ("GetMethod", "lookalike"), ("GetMethods", "lookalike"),
        ("GetField", "lookalike"), ("GetFields", "lookalike"),
    ];

    /// <summary>
    /// Selects the BCL method spelling independently of the overload display suffix.
    /// </summary>
    /// <param name="api">The lookup method and optional overload suffix.</param>
    /// <returns>The exact BCL method name.</returns>
    public static string ApiName(string api) => api.Split('(')[0];

    /// <summary>
    /// Creates a source PE whose original owner discovers and executes an actual global method or reads an actual global field.
    /// </summary>
    /// <param name="api">The global member lookup and optional overload suffix.</param>
    /// <param name="dispatch">The direct, reflected, delegate, pointer, token or lookalike route.</param>
    /// <returns>The independently executable source image.</returns>
    public static byte[] Create(string api, string dispatch)
    {
        var metadata = new MetadataBuilder();
        var name = "GlobalSource" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(7, 8, 9, 10), default, default, 0, AssemblyHashAlgorithm.None);
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
            else if (type == typeof(byte))
            {
                encoder.Byte();
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
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("ModuleGlobals"), metadata.GetOrAddString("Owner"),
            TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(2), MetadataTokens.MethodDefinitionHandle(2));
        byte[] fieldSignature = [6, 8];
        var field = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.HasFieldRVA,
            metadata.GetOrAddString("GlobalData"), metadata.GetOrAddBlob(fieldSignature));
        metadata.AddFieldRelativeVirtualAddress(field, 0);
        var fieldData = new BlobBuilder();
        fieldData.WriteInt32(42);
        var apiName = ApiName(api);
        var isField = apiName.Contains("Field", StringComparison.Ordinal);
        var plural = apiName.EndsWith('s');
        Type[] parameterTypes = api switch
        {
            "GetMethod(types)" => [typeof(string), typeof(Type[])],
            "GetMethod(binding)" => [typeof(string), typeof(BindingFlags), typeof(Binder), typeof(CallingConventions),
                typeof(Type[]), typeof(ParameterModifier[])],
            "GetMethods(flags)" or "GetFields(flags)" => [typeof(BindingFlags)],
            "GetField(flags)" => [typeof(string), typeof(BindingFlags)],
            _ when !plural => [typeof(string)],
            _ => [],
        };
        var lookup = typeof(Module).GetMethod(apiName, parameterTypes)!;
        var instructions = new InstructionEncoder(new BlobBuilder());
        void Call(MethodBase method)
        {
            instructions.OpCode(method.IsStatic ? ILOpCode.Call : ILOpCode.Callvirt);
            instructions.Token(MethodReference(method));
        }

        void LoadModule()
        {
            Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
            Call(typeof(Assembly).GetProperty(nameof(Assembly.ManifestModule))!.GetMethod!);
        }

        void LoadArguments()
        {
            if (!plural)
            {
                instructions.LoadString(metadata.GetOrAddUserString(isField ? "GlobalData" : "GlobalValue"));
            }

            if (api.EndsWith("(flags)", StringComparison.Ordinal) || api.EndsWith("(binding)", StringComparison.Ordinal))
            {
                instructions.LoadConstantI4((int)(BindingFlags.Public | BindingFlags.Static));
            }

            if (api == "GetMethod(binding)")
            {
                instructions.OpCode(ILOpCode.Ldnull);
                instructions.LoadConstantI4((int)CallingConventions.Any);
            }

            if (api is "GetMethod(types)" or "GetMethod(binding)")
            {
                instructions.LoadConstantI4(0);
                instructions.OpCode(ILOpCode.Newarr);
                instructions.Token(TypeReference(typeof(Type)));
            }

            if (api == "GetMethod(binding)")
            {
                instructions.OpCode(ILOpCode.Ldnull);
            }
        }

        if (dispatch == "token")
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(lookup));
            instructions.OpCode(ILOpCode.Pop);
            instructions.LoadConstantI4(42);
        }
        else if (dispatch == "lookalike")
        {
            instructions.Call(MetadataTokens.MethodDefinitionHandle(3));
        }
        else
        {
            if (dispatch == "invoke")
            {
                instructions.OpCode(ILOpCode.Ldtoken);
                instructions.Token(MethodReference(lookup));
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                LoadModule();
                if (plural)
                {
                    instructions.OpCode(ILOpCode.Ldnull);
                }
                else
                {
                    instructions.LoadConstantI4(1);
                    instructions.OpCode(ILOpCode.Newarr);
                    instructions.Token(TypeReference(typeof(object)));
                    instructions.OpCode(ILOpCode.Dup);
                    instructions.LoadConstantI4(0);
                    LoadArguments();
                    instructions.OpCode(ILOpCode.Stelem_ref);
                }

                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                instructions.OpCode(ILOpCode.Castclass);
                instructions.Token(SignatureType(lookup.ReturnType));
            }
            else if (dispatch is "delegate" or "pointer")
            {
                var delegateType = typeof(Func<>).MakeGenericType(lookup.ReturnType);
                if (dispatch == "delegate")
                {
                    instructions.OpCode(ILOpCode.Ldtoken);
                    instructions.Token(SignatureType(delegateType));
                    Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                    LoadModule();
                    instructions.LoadString(metadata.GetOrAddUserString(apiName));
                    Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(object), typeof(string)])!);
                    instructions.OpCode(ILOpCode.Castclass);
                    instructions.Token(SignatureType(delegateType));
                }
                else
                {
                    LoadModule();
                    instructions.OpCode(ILOpCode.Ldftn);
                    instructions.Token(MethodReference(lookup));
                    instructions.OpCode(ILOpCode.Newobj);
                    instructions.Token(MethodReference(delegateType.GetConstructors().Single()));
                }

                Call(delegateType.GetMethod("Invoke")!);
            }
            else
            {
                LoadModule();
                LoadArguments();
                Call(lookup);
            }

            if (plural)
            {
                instructions.LoadConstantI4(0);
                instructions.OpCode(ILOpCode.Ldelem_ref);
            }

            instructions.OpCode(ILOpCode.Ldnull);
            if (isField)
            {
                Call(typeof(FieldInfo).GetMethod(nameof(FieldInfo.GetValue), [typeof(object)])!);
            }
            else
            {
                instructions.OpCode(ILOpCode.Ldnull);
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
            }

            instructions.OpCode(ILOpCode.Unbox_any);
            instructions.Token(TypeReference(typeof(int)));
        }

        instructions.OpCode(ILOpCode.Ret);
        var bodies = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(bodies);
        byte[] readSignature = [0, 0, 8];
        var global = new InstructionEncoder(new BlobBuilder());
        global.LoadConstantI4(42);
        global.OpCode(ILOpCode.Ret);
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("GlobalValue"), metadata.GetOrAddBlob(readSignature), encoder.AddMethodBody(global),
            MetadataTokens.ParameterHandle(1));
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(readSignature), encoder.AddMethodBody(instructions, maxStack: 8),
            MetadataTokens.ParameterHandle(1));
        if (dispatch == "lookalike")
        {
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString(apiName), metadata.GetOrAddBlob(readSignature), encoder.AddMethodBody(global),
                MetadataTokens.ParameterHandle(1));
        }

        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, mappedFieldData: fieldData, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
