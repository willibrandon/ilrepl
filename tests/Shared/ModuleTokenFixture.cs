using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits source metadata rows and real numeric token resolution with expectations independent of copied token layouts.
/// </summary>
public static class ModuleTokenFixture
{
    /// <summary>
    /// Explains why source numeric metadata tokens cannot resolve against a generated module layout.
    /// </summary>
    public const string Problem = "module token resolution cannot reproduce the original metadata tokens";

    /// <summary>
    /// Covers every public resolver overload and handle alias plus representative indirect invocation routes.
    /// </summary>
    public static IReadOnlyList<(string Target, string Api, string Dispatch)> Cases { get; } =
    [
        ("Module", "ResolveMethod", "direct"), ("Module", "ResolveMethod(context)", "direct"),
        ("Module", "ResolveField", "direct"), ("Module", "ResolveField(context)", "direct"),
        ("Module", "ResolveType", "direct"), ("Module", "ResolveType(context)", "direct"),
        ("Module", "ResolveMember", "direct"), ("Module", "ResolveMember(context)", "direct"),
        ("Module", "ResolveString", "direct"), ("Module", "ResolveSignature", "direct"),
        ("ModuleHandle", "ResolveMethodHandle", "direct"), ("ModuleHandle", "ResolveMethodHandle(context)", "direct"),
        ("ModuleHandle", "ResolveFieldHandle", "direct"), ("ModuleHandle", "ResolveFieldHandle(context)", "direct"),
        ("ModuleHandle", "ResolveTypeHandle", "direct"), ("ModuleHandle", "ResolveTypeHandle(context)", "direct"),
        ("ModuleHandle", "GetRuntimeMethodHandleFromMetadataToken", "direct"),
        ("ModuleHandle", "GetRuntimeFieldHandleFromMetadataToken", "direct"),
        ("ModuleHandle", "GetRuntimeTypeHandleFromMetadataToken", "direct"),
        ("Module", "ResolveString", "invoke"), ("Module", "ResolveString", "delegate"),
        ("ModuleHandle", "ResolveTypeHandle", "invoke"),
    ];

    /// <summary>
    /// Standalone method tokens, user lookalikes and ordinary name-based member lookup remain supported.
    /// </summary>
    public static IReadOnlyList<(string Target, string Api, string Dispatch)> SupportedCases { get; } =
    [
        ("Module", "ResolveString", "token"), ("Module", "ResolveSignature", "token"),
        ("ModuleHandle", "ResolveTypeHandle", "token"), ("Module", "ResolveMethod", "lookalike"),
        ("Module", "ResolveString", "lookalike"), ("ModuleHandle", "ResolveTypeHandle", "lookalike"),
        ("Module", "ResolveMethod", "lookup"), ("Module", "ResolveField", "lookup"),
    ];

    /// <summary>
    /// Selects the exact resolver method spelling independently of its overload display suffix.
    /// </summary>
    /// <param name="api">The resolver and optional generic-context suffix.</param>
    /// <returns>The BCL method name.</returns>
    public static string ApiName(string api) => api.EndsWith("(context)", StringComparison.Ordinal) ? api[..^9] : api;

    /// <summary>
    /// Creates a real PE with deliberately addressed method, field, type, user-string and standalone-signature rows.
    /// </summary>
    /// <param name="target">The Module or ModuleHandle receiver.</param>
    /// <param name="api">The resolver and optional generic-context overload.</param>
    /// <param name="dispatch">The direct, reflected, delegate, standalone-token, lookalike or ordinary-lookup route.</param>
    /// <returns>The independently executable source image.</returns>
    public static byte[] Create(string target, string api, string dispatch)
    {
        var metadata = new MetadataBuilder();
        var name = "TokenSource" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(7, 8, 9, 10), default, default, 0, AssemblyHashAlgorithm.None);
        var text = metadata.GetOrAddUserString("source token text");
        byte[] signatureBytes = [7, 1, 8];
        var signatureToken = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(signatureBytes));
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
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("ModuleTokens"), metadata.GetOrAddString("Owner"),
            TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var hidden = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("ModuleTokens"),
            metadata.GetOrAddString("HiddenType"), TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(2));
        byte[] fieldSignature = [6, 8];
        var field = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, metadata.GetOrAddString("HiddenField"),
            metadata.GetOrAddBlob(fieldSignature));
        var apiName = ApiName(api);
        var receiver = target == "ModuleHandle" ? typeof(ModuleHandle) : typeof(Module);
        var context = api.EndsWith("(context)", StringComparison.Ordinal);
        var contextType = target == "ModuleHandle" ? typeof(RuntimeTypeHandle[]) : typeof(Type[]);
        var resolver = receiver.GetMethod(apiName, context ? [typeof(int), contextType, contextType] : [typeof(int)])!;
        var token = apiName switch
        {
            "ResolveString" => MetadataTokens.GetToken(text),
            "ResolveSignature" => MetadataTokens.GetToken(signatureToken),
            _ when apiName.Contains("Field", StringComparison.Ordinal) => MetadataTokens.GetToken(field),
            _ when apiName.Contains("Type", StringComparison.Ordinal) => MetadataTokens.GetToken(hidden),
            _ => MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(2)),
        };
        var expected = apiName switch
        {
            "ResolveString" => "source token text", "ResolveSignature" => "BwEI",
            _ when apiName.Contains("Field", StringComparison.Ordinal) => "HiddenField",
            _ when apiName.Contains("Type", StringComparison.Ordinal) => "HiddenType", _ => "HiddenMethod",
        };
        if (dispatch == "lookalike") expected = "user token result";
        var instructions = new InstructionEncoder(new BlobBuilder());
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

        void LoadModule(bool boxed)
        {
            Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
            Call(typeof(Assembly).GetProperty(nameof(Assembly.ManifestModule))!.GetMethod!);
            if (target != "ModuleHandle") return;
            Call(typeof(Module).GetProperty(nameof(Module.ModuleHandle))!.GetMethod!);
            if (boxed)
            {
                instructions.OpCode(ILOpCode.Box);
                instructions.Token(TypeReference(typeof(ModuleHandle)));
            }
            else
            {
                instructions.StoreLocal(0);
                instructions.LoadLocalAddress(0);
            }
        }

        if (dispatch == "token")
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(resolver));
            instructions.OpCode(ILOpCode.Pop);
            instructions.LoadConstantI4(42);
        }
        else
        {
            if (dispatch == "lookalike") instructions.Call(MetadataTokens.MethodDefinitionHandle(3));
            else
            {
                if (dispatch == "lookup")
                {
                    LoadType(hidden);
                    instructions.LoadString(metadata.GetOrAddUserString(expected));
                    Call(apiName == "ResolveField" ? typeof(Type).GetMethod(nameof(Type.GetField), [typeof(string)])!
                        : typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string)])!);
                }
                else if (dispatch == "invoke")
                {
                    instructions.OpCode(ILOpCode.Ldtoken);
                    instructions.Token(MethodReference(resolver));
                    Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                    LoadModule(boxed: true);
                    instructions.LoadConstantI4(1);
                    instructions.OpCode(ILOpCode.Newarr);
                    instructions.Token(TypeReference(typeof(object)));
                    instructions.OpCode(ILOpCode.Dup);
                    instructions.LoadConstantI4(0);
                    instructions.LoadConstantI4(token);
                    instructions.OpCode(ILOpCode.Box);
                    instructions.Token(TypeReference(typeof(int)));
                    instructions.OpCode(ILOpCode.Stelem_ref);
                    Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                    instructions.OpCode(resolver.ReturnType.IsValueType ? ILOpCode.Unbox_any : ILOpCode.Castclass);
                    instructions.Token(SignatureType(resolver.ReturnType));
                }
                else if (dispatch == "delegate")
                {
                    LoadType(SignatureType(typeof(Func<int, string>)));
                    LoadModule(boxed: false);
                    instructions.LoadString(metadata.GetOrAddUserString(apiName));
                    Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(object), typeof(string)])!);
                    instructions.OpCode(ILOpCode.Castclass);
                    instructions.Token(SignatureType(typeof(Func<int, string>)));
                    instructions.LoadConstantI4(token);
                    Call(typeof(Func<int, string>).GetMethod(nameof(Func<int, string>.Invoke))!);
                }
                else
                {
                    LoadModule(boxed: false);
                    instructions.LoadConstantI4(token);
                    if (context)
                    {
                        instructions.OpCode(ILOpCode.Ldnull);
                        instructions.OpCode(ILOpCode.Ldnull);
                    }
                    if (target == "ModuleHandle") instructions.Call(MethodReference(resolver));
                    else Call(resolver);
                }
                if (target == "ModuleHandle")
                {
                    if (resolver.ReturnType == typeof(RuntimeMethodHandle))
                        Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                    else if (resolver.ReturnType == typeof(RuntimeFieldHandle))
                        Call(typeof(FieldInfo).GetMethod(nameof(FieldInfo.GetFieldFromHandle), [typeof(RuntimeFieldHandle)])!);
                    else Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                }
                if (apiName == "ResolveSignature") Call(typeof(Convert).GetMethod(nameof(Convert.ToBase64String), [typeof(byte[])])!);
                else if (apiName != "ResolveString") Call(typeof(MemberInfo).GetProperty(nameof(MemberInfo.Name))!.GetMethod!);
            }
            instructions.LoadString(metadata.GetOrAddUserString(expected));
            Call(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Mul);
        }
        instructions.OpCode(ILOpCode.Ret);
        var locals = new BlobBuilder();
        new BlobEncoder(locals).LocalVariableSignature(1).AddVariable().Type().Type(TypeReference(typeof(ModuleHandle)), true);
        var bodies = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(bodies);
        var offset = encoder.AddMethodBody(instructions, maxStack: 8,
            localVariablesSignature: metadata.AddStandaloneSignature(metadata.GetOrAddBlob(locals)));
        byte[] readSignature = [0, 0, 8];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(readSignature), offset, MetadataTokens.ParameterHandle(1));
        var helper = new InstructionEncoder(new BlobBuilder());
        helper.LoadConstantI4(42);
        helper.OpCode(ILOpCode.Ret);
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("HiddenMethod"), metadata.GetOrAddBlob(readSignature), encoder.AddMethodBody(helper),
            MetadataTokens.ParameterHandle(1));
        if (dispatch == "lookalike")
        {
            helper = new InstructionEncoder(new BlobBuilder());
            helper.LoadString(metadata.GetOrAddUserString(expected));
            helper.OpCode(ILOpCode.Ret);
            byte[] helperSignature = [0, 0, 14];
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString(apiName), metadata.GetOrAddBlob(helperSignature), encoder.AddMethodBody(helper),
                MetadataTokens.ParameterHandle(1));
        }
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
