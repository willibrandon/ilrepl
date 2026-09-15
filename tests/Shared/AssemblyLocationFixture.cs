using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits real source-file and image metadata observations with independent expectations for original assembly execution.
/// </summary>
public static class AssemblyLocationFixture
{
    /// <summary>
    /// The source metadata scope differs deliberately from both the real filename and generated module names.
    /// </summary>
    public const string Scope = "source-metadata.netmodule";

    /// <summary>
    /// The nondefault image version verifies that workers retain source PE metadata rather than generating it anew.
    /// </summary>
    public const string ImageVersion = "v2.0.50727";

    /// <summary>
    /// The bounded direct API inventory and representative reflection/delegate dispatches.
    /// </summary>
    public static IReadOnlyList<(string Target, string Api, string Dispatch)> Cases { get; } =
    [
        ("Assembly", "Location", "direct"), ("Assembly", "Location", "property"),
        ("Assembly", "Location", "delegate"), ("Assembly", "CodeBase", "direct"),
        ("Assembly", "EscapedCodeBase", "direct"), ("Assembly", "ImageRuntimeVersion", "direct"),
        ("Assembly", "IsDynamic", "direct"), ("Assembly", "IsCollectible", "direct"),
        ("Assembly", "EntryPoint", "direct"), ("Assembly", "GetFile", "direct"),
        ("Assembly", "GetFiles", "direct"), ("Assembly", "GetFiles(bool)", "direct"),
        ("Module", "Name", "direct"), ("Module", "ScopeName", "direct"),
        ("Module", "FullyQualifiedName", "direct"), ("Module", "ModuleVersionId", "direct"),
        ("Module", "MDStreamVersion", "direct"), ("Module", "GetPEKind", "direct"),
        ("Module", "ToString", "direct"), ("Module", "ToString", "object"), ("Module", "ScopeName", "property"),
        ("ModuleHandle", "MDStreamVersion", "direct"),
        ("Assembly", "GetModule", "direct"), ("Assembly", "GetModules", "direct"),
        ("Assembly", "GetModules(bool)", "direct"), ("Assembly", "GetLoadedModules", "direct"),
        ("Assembly", "GetLoadedModules(bool)", "direct"), ("Assembly", "Modules", "direct"),
        ("Assembly", "GetModules", "invoke"), ("Assembly", "GetLoadedModules", "table-delegate"),
        ("Assembly", "GetModule", "helper"),
    ];

    /// <summary>
    /// Stable runtime constants and ordinary metadata controls remain supported.
    /// </summary>
    public static IReadOnlyList<(string Target, string Api, string Dispatch)> SupportedCases { get; } =
    [
        ("Assembly", "ReflectionOnly", "direct"), ("Assembly", "GlobalAssemblyCache", "direct"),
        ("Assembly", "HostContext", "direct"), ("Assembly", "IsFullyTrusted", "direct"),
        ("Assembly", "Location", "token"), ("Module", "ModuleVersionId", "token"),
        ("Assembly", "Location", "lookalike"), ("Module", "Name", "lookalike"),
        ("Type", "Name", "direct"),
        ("Assembly", "GetModule", "token"), ("Assembly", "GetLoadedModules(bool)", "token"),
        ("Assembly", "Modules", "token"), ("Assembly", "GetModule", "lookalike"),
        ("Assembly", "GetLoadedModules", "lookalike"), ("Assembly", "Modules", "lookalike"),
    ];

    /// <summary>
    /// Selects the exact reason for one unsupported BCL source or image observation.
    /// </summary>
    /// <param name="target">The Assembly, Module, or ModuleHandle receiver.</param>
    /// <param name="api">The actual property or method.</param>
    /// <returns>The guard's expected explanation.</returns>
    public static string Problem(string target, string api) => target is "Module" or "ModuleHandle"
        ? "module identity inspection cannot reproduce the original module metadata"
        : IsModuleTable(api) ? "assembly module inspection cannot reproduce the original module table"
        : FileDependent(target, api) ? "assembly file inspection cannot reproduce the original assembly file context"
        : AssemblyIdentityFixture.Problem;

    /// <summary>
    /// Identifies the exact module-table APIs whose source contents generated copies cannot reproduce.
    /// </summary>
    /// <param name="api">The selected property or method spelling.</param>
    /// <returns>Whether this operation observes the assembly's module table.</returns>
    public static bool IsModuleTable(string api) => api is "GetModule" or "GetModules" or "GetModules(bool)"
        or "GetLoadedModules" or "GetLoadedModules(bool)" or "Modules";

    /// <summary>
    /// Identifies source observations that a file-loaded original cannot retain when reloaded from captured bytes.
    /// </summary>
    /// <param name="target">The Assembly or Module receiver.</param>
    /// <param name="api">The property or method being inspected.</param>
    /// <returns>Whether the original's path is part of its result.</returns>
    public static bool FileDependent(string target, string api) => target == "Module"
        ? api is "Name" or "FullyQualifiedName"
        : api is "Location" or "CodeBase" or "EscapedCodeBase" or "GetFile" or "GetFiles" or "GetFiles(bool)";

    /// <summary>
    /// Observes the actual session assembly's collectibility before any comparison image is generated.
    /// </summary>
    /// <param name="collectible">Whether the runtime's session loader uses a collectible assembly.</param>
    /// <returns>The complete source method whose correct original observation returns forty-two.</returns>
    public static string CollectibleSource(bool collectible) => ".method public static int32 Read() {\n"
        + "call class Assembly Assembly::GetExecutingAssembly()\ncallvirt instance bool Assembly::get_IsCollectible()\n"
        + (collectible ? "ldc.i4.1\n" : "ldc.i4.0\n") + "ceq\nldc.i4.s 42\nmul\nret\n}";

    /// <summary>
    /// Creates a source image with distinct module identity, image version, and a real entry point.
    /// </summary>
    /// <param name="target">The Assembly, Module, ModuleHandle, or ordinary Type receiver.</param>
    /// <param name="api">The actual property or method.</param>
    /// <param name="dispatch">The direct, reflected, delegate, token, or lookalike form.</param>
    /// <param name="path">The actual source filename that the desktop loader will map.</param>
    /// <param name="stream">Whether the browser's actual source loader reads bytes rather than mapping that file.</param>
    /// <param name="machine">The desktop process architecture, or I386 for portable browser IL.</param>
    /// <returns>The real portable image and its independently specified observation.</returns>
    public static (byte[] Image, string Expected) Create(string target, string api, string dispatch, string path,
        bool stream = false, Machine machine = Machine.I386)
    {
        var metadata = new MetadataBuilder();
        var name = "LocationSource" + Guid.NewGuid().ToString("N");
        var mvid = Guid.NewGuid();
        metadata.AddModule(0, metadata.GetOrAddString(Scope), metadata.GetOrAddGuid(mvid), default, default);
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
            else if (type == typeof(int)) encoder.Int32();
            else if (type == typeof(long)) encoder.Int64();
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
                    foreach (var parameter in parameters)
                        EncodeType(arguments.AddParameter().Type(parameter.ParameterType.IsByRef),
                            parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType);
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
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("SourceInspection"),
            metadata.GetOrAddString("Owner"), TypeReference(typeof(object)),
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var receiver = target switch
        {
            "Module" => typeof(Module), "ModuleHandle" => typeof(ModuleHandle), "Type" => typeof(Type), _ => typeof(Assembly),
        };
        var inspection = api switch
        {
            "GetFile" or "GetModule" => receiver.GetMethod(api, [typeof(string)])!,
            "GetFiles" or "GetModules" or "GetLoadedModules" => receiver.GetMethod(api, Type.EmptyTypes)!,
            "GetFiles(bool)" => receiver.GetMethod("GetFiles", [typeof(bool)])!,
            "GetModules(bool)" => receiver.GetMethod("GetModules", [typeof(bool)])!,
            "GetLoadedModules(bool)" => receiver.GetMethod("GetLoadedModules", [typeof(bool)])!,
            "GetPEKind" => receiver.GetMethod(api)!,
            "ToString" => receiver.GetMethod(api, Type.EmptyTypes)!,
            _ => receiver.GetProperty(api)!.GetMethod!,
        };
        var expected = api switch
        {
            "Location" or "FullyQualifiedName" => stream ? "" : path,
            "CodeBase" or "EscapedCodeBase" => new Uri(path).AbsoluteUri,
            "Name" => target == "Type" ? "String" : Path.GetFileName(path),
            "ScopeName" or "ToString" => Scope,
            "ImageRuntimeVersion" => ImageVersion,
            "ModuleVersionId" => mvid.ToString(),
            // Mono reports the metadata root version (1.1); CoreCLR reports the tables stream version (2.0).
            "MDStreamVersion" => stream ? "65537" : "131072",
            "GetPEKind" => machine switch { Machine.Amd64 => "534404", Machine.Arm64 => "543620", _ => "100332" },
            "IsDynamic" or "IsCollectible" or "ReflectionOnly" or "GlobalAssemblyCache" => "False",
            "IsFullyTrusted" => "True",
            "HostContext" => "0",
            "EntryPoint" => "Main",
            _ => path,
        };
        if (IsModuleTable(api)) expected = Scope;
        if (dispatch == "lookalike") expected = "user metadata";
        var instructions = new InstructionEncoder(new BlobBuilder());
        void Call(MethodBase method)
        {
            instructions.OpCode(method.IsStatic ? ILOpCode.Call : ILOpCode.Callvirt);
            instructions.Token(MethodReference(method));
        }

        void LoadType(Type type)
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(SignatureType(type));
            Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        }

        void LoadReceiver()
        {
            if (target == "Type") LoadType(typeof(string));
            else
            {
                Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
                if (target is "Module" or "ModuleHandle")
                    Call(typeof(Assembly).GetProperty(nameof(Assembly.ManifestModule))!.GetMethod!);
                if (target == "ModuleHandle")
                {
                    Call(typeof(Module).GetProperty(nameof(Module.ModuleHandle))!.GetMethod!);
                    instructions.StoreLocal(3);
                    instructions.LoadLocalAddress(3);
                }
            }
        }

        if (dispatch == "token")
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(inspection));
            instructions.OpCode(ILOpCode.Pop);
            instructions.LoadConstantI4(42);
        }
        else
        {
            if (dispatch == "lookalike") instructions.Call(MetadataTokens.MethodDefinitionHandle(3));
            else
            {
                if (dispatch == "property")
                {
                    LoadType(receiver);
                    instructions.LoadString(metadata.GetOrAddUserString(api));
                    Call(typeof(Type).GetMethod(nameof(Type.GetProperty), [typeof(string)])!);
                    LoadReceiver();
                    Call(typeof(PropertyInfo).GetMethod(nameof(PropertyInfo.GetValue), [typeof(object)])!);
                    instructions.OpCode(ILOpCode.Castclass);
                    instructions.Token(TypeReference(typeof(string)));
                }
                else if (dispatch == "invoke")
                {
                    instructions.OpCode(ILOpCode.Ldtoken);
                    instructions.Token(MethodReference(inspection));
                    Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                    LoadReceiver();
                    instructions.OpCode(ILOpCode.Ldnull);
                    Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                    instructions.OpCode(ILOpCode.Castclass);
                    instructions.Token(SignatureType(typeof(Module[])));
                }
                else if (dispatch is "delegate" or "table-delegate")
                {
                    LoadType(dispatch == "table-delegate" ? typeof(Func<Module[]>) : typeof(Func<string>));
                    LoadReceiver();
                    instructions.LoadString(metadata.GetOrAddUserString(inspection.Name));
                    Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(object), typeof(string)])!);
                    instructions.OpCode(ILOpCode.Ldnull);
                    Call(typeof(Delegate).GetMethod(nameof(Delegate.DynamicInvoke))!);
                    instructions.OpCode(ILOpCode.Castclass);
                    instructions.Token(dispatch == "table-delegate" ? SignatureType(typeof(Module[])) : TypeReference(typeof(string)));
                }
                else if (dispatch == "helper")
                {
                    instructions.LoadArgument(0);
                    instructions.Call(MetadataTokens.MethodDefinitionHandle(3));
                }
                else
                {
                    LoadReceiver();
                    if (api is "GetFile" or "GetModule") instructions.LoadString(metadata.GetOrAddUserString(Scope));
                    if (api.EndsWith("(bool)", StringComparison.Ordinal)) instructions.LoadConstantI4(1);
                    if (api == "GetPEKind")
                    {
                        instructions.LoadLocalAddress(1);
                        instructions.LoadLocalAddress(2);
                    }
                    if (target == "ModuleHandle") instructions.Call(MethodReference(inspection));
                    else Call(dispatch == "object" ? typeof(object).GetMethod(nameof(ToString))! : inspection);
                    if (api == "GetPEKind")
                    {
                        instructions.LoadLocal(1);
                        instructions.LoadConstantI4(100000);
                        instructions.OpCode(ILOpCode.Mul);
                        instructions.LoadLocal(2);
                        instructions.OpCode(ILOpCode.Add);
                        instructions.OpCode(ILOpCode.Box);
                        instructions.Token(TypeReference(typeof(int)));
                        Call(typeof(object).GetMethod(nameof(ToString))!);
                    }
                    else if (api.StartsWith("GetFile", StringComparison.Ordinal))
                    {
                        if (api != "GetFile")
                        {
                            instructions.LoadConstantI4(0);
                            instructions.OpCode(ILOpCode.Ldelem_ref);
                        }
                        instructions.StoreLocal(0);
                        instructions.LoadLocal(0);
                        Call(typeof(FileStream).GetProperty(nameof(FileStream.Name))!.GetMethod!);
                        instructions.LoadLocal(0);
                        Call(typeof(Stream).GetMethod(nameof(Stream.Dispose), Type.EmptyTypes)!);
                    }
                    else if (api == "EntryPoint") Call(typeof(MemberInfo).GetProperty(nameof(MemberInfo.Name))!.GetMethod!);
                    else if (inspection.ReturnType.IsValueType)
                    {
                        instructions.OpCode(ILOpCode.Box);
                        instructions.Token(TypeReference(inspection.ReturnType));
                        Call(typeof(object).GetMethod(nameof(ToString))!);
                    }
                }

                if (IsModuleTable(api))
                {
                    if (api == "GetModule")
                    {
                        instructions.OpCode(ILOpCode.Ldnull);
                        instructions.OpCode(ILOpCode.Ceq);
                        instructions.LoadConstantI4(0);
                    }
                    else if (api == "Modules")
                    {
                        Call(typeof(Enumerable).GetMethods().Single(method => method.Name == nameof(Enumerable.Count)
                            && method.GetParameters().Length == 1).MakeGenericMethod(typeof(Module)));
                        instructions.LoadConstantI4(1);
                    }
                    else
                    {
                        instructions.OpCode(ILOpCode.Ldlen);
                        instructions.OpCode(ILOpCode.Conv_i4);
                        instructions.LoadConstantI4(1);
                    }
                    instructions.OpCode(ILOpCode.Ceq);
                }
            }

            if (!IsModuleTable(api) || dispatch == "lookalike")
            {
                instructions.LoadArgument(0);
                Call(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
            }
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Mul);
        }

        instructions.OpCode(ILOpCode.Ret);
        var locals = new BlobBuilder();
        var variables = new BlobEncoder(locals).LocalVariableSignature(4);
        EncodeType(variables.AddVariable().Type(), typeof(FileStream));
        EncodeType(variables.AddVariable().Type(), typeof(PortableExecutableKinds));
        EncodeType(variables.AddVariable().Type(), typeof(ImageFileMachine));
        EncodeType(variables.AddVariable().Type(), typeof(ModuleHandle));
        var bodies = new BlobBuilder();
        var bodyEncoder = new MethodBodyStreamEncoder(bodies);
        var offset = bodyEncoder.AddMethodBody(instructions, maxStack: 5,
            localVariablesSignature: metadata.AddStandaloneSignature(metadata.GetOrAddBlob(locals)));
        byte[] signature = [0, 1, 8, 14];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(signature), offset, MetadataTokens.ParameterHandle(1));
        var main = new InstructionEncoder(new BlobBuilder());
        main.LoadConstantI4(42);
        main.OpCode(ILOpCode.Ret);
        byte[] mainSignature = [0, 0, 8];
        var entry = metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Main"), metadata.GetOrAddBlob(mainSignature), bodyEncoder.AddMethodBody(main),
            MetadataTokens.ParameterHandle(1));
        if (dispatch == "lookalike")
        {
            var helper = new InstructionEncoder(new BlobBuilder());
            helper.LoadString(metadata.GetOrAddUserString(expected));
            helper.OpCode(ILOpCode.Ret);
            byte[] helperSignature = [0, 0, 14];
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString(inspection.Name), metadata.GetOrAddBlob(helperSignature), bodyEncoder.AddMethodBody(helper),
                MetadataTokens.ParameterHandle(1));
        }
        else if (dispatch == "helper")
        {
            var helper = new InstructionEncoder(new BlobBuilder());
            helper.Call(MethodReference(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!));
            helper.LoadArgument(0);
            helper.OpCode(ILOpCode.Callvirt);
            helper.Token(MethodReference(inspection));
            helper.OpCode(ILOpCode.Ret);
            var helperSignature = new BlobBuilder();
            new BlobEncoder(helperSignature).MethodSignature().Parameters(1,
                result => EncodeType(result.Type(), typeof(Module)), parameters => parameters.AddParameter().Type().String());
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString("FindSourceModule"), metadata.GetOrAddBlob(helperSignature), bodyEncoder.AddMethodBody(helper),
                MetadataTokens.ParameterHandle(1));
        }

        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(machine: machine, imageCharacteristics: Characteristics.ExecutableImage),
            new MetadataRootBuilder(metadata, metadataVersion: ImageVersion), bodies, entryPoint: entry,
            flags: CorFlags.ILOnly).Serialize(image);
        return (image.ToArray(), expected);
    }
}
