using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits actual type-name queries with independently prescribed source spellings and safe name controls.
/// </summary>
public static class TypeNameFixture
{
    /// <summary>
    /// Identifies the precise refusal for a type name that cannot retain its source identity after copying.
    /// </summary>
    public const string Problem = "type name inspection cannot reproduce the original copied type name";

    /// <summary>
    /// Enumerates changed names, representative copied shapes, indirect dispatch and an unproven receiver.
    /// </summary>
    public static IReadOnlyList<(string Shape, string Api, string Dispatch)> Cases { get; } =
    [
        ("owner", "FullName", "direct"), ("owner", "Namespace", "direct"),
        ("owner", "AssemblyQualifiedName", "direct"), ("owner", "ToString", "direct"),
        ("nested", "FullName", "direct"), ("array", "AssemblyQualifiedName", "direct"),
        ("array", "Namespace", "direct"), ("auxiliary-array", "Name", "direct"),
        ("constructed", "FullName", "direct"), ("constructed", "ToString", "direct"),
        ("auxiliary", "Name", "direct"), ("auxiliary", "Namespace", "direct"),
        ("owner", "FullName", "invoke"), ("owner", "AssemblyQualifiedName", "property"),
        ("owner", "Namespace", "invoke-member"), ("owner", "ToString", "delegate"),
        ("owner", "FullName", "named"), ("owner", "AssemblyQualifiedName", "pointer"),
        ("owner", "ToString", "object"), ("unknown", "FullName", "direct"),
    ];

    /// <summary>
    /// Enumerates unchanged external and copied names, null results, null receivers and ordinary member metadata.
    /// </summary>
    public static IReadOnlyList<(string Shape, string Api, string Dispatch)> SupportedCases { get; } =
    [
        ("bcl", "FullName", "direct"), ("bcl", "Namespace", "direct"),
        ("bcl", "AssemblyQualifiedName", "direct"), ("bcl", "ToString", "object"),
        ("sibling", "FullName", "direct"), ("sibling", "Namespace", "direct"),
        ("sibling", "AssemblyQualifiedName", "direct"), ("sibling", "ToString", "direct"),
        ("bcl", "FullName", "invoke"), ("sibling", "AssemblyQualifiedName", "property"),
        ("bcl", "Namespace", "invoke-member"), ("sibling", "ToString", "delegate"),
        ("bcl", "FullName", "named"), ("sibling", "AssemblyQualifiedName", "pointer"),
        ("owner", "Name", "direct"), ("nested", "Name", "direct"), ("array", "Name", "direct"),
        ("constructed", "Name", "direct"), ("constructed", "Namespace", "direct"),
        ("parameter", "FullName", "direct"), ("parameter", "AssemblyQualifiedName", "direct"),
        ("parameter", "Name", "direct"), ("parameter", "ToString", "direct"),
        ("null", "FullName", "direct"), ("owner", "FullName", "token"),
        ("owner", "FullName", "lookalike"), ("member", "Name", "direct"),
        ("ordinary", "ToString", "object"),
    ];

    /// <summary>
    /// Creates a real source assembly whose Read method returns forty-two only for the prescribed original name.
    /// </summary>
    /// <param name="shape">The queried source, external, constructed or null type.</param>
    /// <param name="api">The public reflection name API.</param>
    /// <param name="dispatch">The direct, reflected, delegate or supported control route.</param>
    /// <returns>The source image, its simple assembly name and the independently prescribed queried string.</returns>
    public static (byte[] Image, string AssemblyName, string? Expected) Create(string shape, string api, string dispatch)
    {
        var metadata = new MetadataBuilder();
        var name = "TypeNameSource" + Guid.NewGuid().ToString("N");
        var identity = name + ", Version=7.8.9.10, Culture=neutral, PublicKeyToken=null";
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(7, 8, 9, 10), default, default, 0, AssemblyHashAlgorithm.None);
        var references = new Dictionary<Assembly, AssemblyReferenceHandle>();
        var types = new Dictionary<Type, TypeReferenceHandle>();
        TypeReferenceHandle TypeReference(Type type)
        {
            if (types.TryGetValue(type, out var handle)) return handle;
            if (!references.TryGetValue(type.Assembly, out var reference))
            {
                var assembly = type.Assembly.GetName();
                reference = metadata.AddAssemblyReference(metadata.GetOrAddString(assembly.Name!), assembly.Version!, default,
                    metadata.GetOrAddBlob(assembly.GetPublicKeyToken()!), 0, default);
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
            else if (type == typeof(string)) encoder.String();
            else if (type == typeof(object)) encoder.Object();
            else if (type == typeof(nint)) encoder.IntPtr();
            else if (type.IsArray) EncodeType(encoder.SZArray(), type.GetElementType()!);
            else if (type.IsGenericParameter) encoder.GenericTypeParameter(type.GenericParameterPosition);
            else if (type.IsGenericType)
            {
                var arguments = type.GetGenericArguments();
                var parameters = encoder.GenericInstantiation(TypeReference(type.GetGenericTypeDefinition()), arguments.Length,
                    type.IsValueType);
                foreach (var argument in arguments) EncodeType(parameters.AddArgument(), argument);
            }
            else encoder.Type(TypeReference(type), type.IsValueType);
        }

        EntityHandle SignatureType(Type type)
        {
            if (!type.IsArray && !type.IsGenericType) return TypeReference(type);
            var signature = new BlobBuilder();
            EncodeType(new BlobEncoder(signature).TypeSpecificationSignature(), type);
            return metadata.AddTypeSpecification(metadata.GetOrAddBlob(signature));
        }

        EntityHandle MethodReference(MethodBase method)
        {
            var declaring = method.DeclaringType!;
            var definition = declaring.IsConstructedGenericType
                ? declaring.GetGenericTypeDefinition().GetMethods().Cast<MethodBase>()
                    .Concat(declaring.GetGenericTypeDefinition().GetConstructors())
                    .Single(candidate => candidate.MetadataToken == method.MetadataToken)
                : method;
            var signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature(isInstanceMethod: !method.IsStatic).Parameters(definition.GetParameters().Length,
                result =>
                {
                    if (definition is not MethodInfo info || info.ReturnType == typeof(void)) result.Void();
                    else EncodeType(result.Type(), info.ReturnType);
                }, arguments =>
                {
                    foreach (var parameter in definition.GetParameters())
                        EncodeType(arguments.AddParameter().Type(), parameter.ParameterType);
                });
            return metadata.AddMemberReference(SignatureType(declaring), metadata.GetOrAddString(method.Name),
                metadata.GetOrAddBlob(signature));
        }

        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("TypeNames"),
            metadata.GetOrAddString("Owner"), TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        var auxiliary = metadata.AddTypeDefinition(TypeAttributes.NotPublic, metadata.GetOrAddString("TypeNames"),
            metadata.GetOrAddString("Auxiliary"), TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(5));
        var sibling = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("TypeNames"),
            metadata.GetOrAddString("Sibling"), TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(5));
        var nested = metadata.AddTypeDefinition(TypeAttributes.NestedPrivate, default, metadata.GetOrAddString("Nested"),
            TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(5));
        metadata.AddNestedType(nested, owner);
        var bodies = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(bodies);
        void Method(string methodName, Type result, InstructionEncoder body, StandaloneSignatureHandle locals = default)
        {
            var signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature(genericParameterCount: methodName == "Probe" ? 1 : 0).Parameters(0,
                returns => EncodeType(returns.Type(), result), _ => { });
            var method = metadata.AddMethodDefinition(MethodAttributes.Static
                | (methodName == "Read" || methodName == "Probe" ? MethodAttributes.Public : MethodAttributes.Private),
                MethodImplAttributes.IL, metadata.GetOrAddString(methodName), metadata.GetOrAddBlob(signature),
                encoder.AddMethodBody(body, maxStack: 12, localVariablesSignature: locals), MetadataTokens.ParameterHandle(1));
            if (methodName == "Probe") metadata.AddGenericParameter(method, 0, metadata.GetOrAddString("T"), 0);
        }

        var expected = Expected(shape, api, dispatch, identity);
        var graph = new ControlFlowBuilder();
        var instructions = new InstructionEncoder(new BlobBuilder(), graph);
        void Call(MethodBase method)
        {
            instructions.OpCode(method.IsStatic ? ILOpCode.Call : ILOpCode.Callvirt);
            instructions.Token(MethodReference(method));
        }
        void LoadString(string? value)
        {
            if (value is null) instructions.OpCode(ILOpCode.Ldnull);
            else instructions.LoadString(metadata.GetOrAddUserString(value));
        }
        if (shape == "null")
        {
            var start = instructions.DefineLabel();
            var end = instructions.DefineLabel();
            var handler = instructions.DefineLabel();
            var handlerEnd = instructions.DefineLabel();
            var done = instructions.DefineLabel();
            instructions.MarkLabel(start);
            instructions.Call(MetadataTokens.MethodDefinitionHandle(2));
            instructions.OpCode(ILOpCode.Pop);
            instructions.LoadConstantI4(0);
            instructions.StoreLocal(0);
            instructions.Branch(ILOpCode.Leave, done);
            instructions.MarkLabel(end);
            instructions.MarkLabel(handler);
            instructions.OpCode(ILOpCode.Pop);
            instructions.LoadConstantI4(42);
            instructions.StoreLocal(0);
            instructions.Branch(ILOpCode.Leave, done);
            instructions.MarkLabel(handlerEnd);
            instructions.MarkLabel(done);
            instructions.LoadLocal(0);
            instructions.OpCode(ILOpCode.Ret);
            graph.AddCatchRegion(start, end, handler, handlerEnd, TypeReference(typeof(NullReferenceException)));
            var signature = new BlobBuilder();
            new BlobEncoder(signature).LocalVariableSignature(1).AddVariable().Type().Int32();
            Method("Read", typeof(int), instructions, metadata.AddStandaloneSignature(metadata.GetOrAddBlob(signature)));
        }
        else
        {
            instructions.Call(MetadataTokens.MethodDefinitionHandle(2));
            LoadString(expected);
            Call(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Mul);
            instructions.OpCode(ILOpCode.Ret);
            Method("Read", typeof(int), instructions);
        }

        instructions = new InstructionEncoder(new BlobBuilder());
        void LoadType(EntityHandle type)
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(type);
            Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        }
        void LoadReceiver()
        {
            if (shape == "null") instructions.OpCode(ILOpCode.Ldnull);
            else if (shape == "ordinary")
            {
                instructions.LoadConstantI4(42);
                instructions.OpCode(ILOpCode.Box);
                instructions.Token(TypeReference(typeof(int)));
            }
            else if (shape is "member" or "parameter")
            {
                instructions.OpCode(ILOpCode.Ldtoken);
                instructions.Token(MetadataTokens.MethodDefinitionHandle(3));
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                if (shape == "parameter")
                {
                    Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetGenericArguments))!);
                    instructions.LoadConstantI4(0);
                    instructions.OpCode(ILOpCode.Ldelem_ref);
                }
            }
            else if (shape == "unknown")
            {
                Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
                LoadString(" TypeNames.Owner ");
                Call(typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!);
                Call(typeof(Assembly).GetMethod(nameof(Assembly.GetType), [typeof(string)])!);
            }
            else if (shape is "array" or "auxiliary-array" or "constructed")
            {
                var signature = new BlobBuilder();
                var encoded = new BlobEncoder(signature).TypeSpecificationSignature();
                if (shape is "array" or "auxiliary-array") encoded.SZArray().Type(shape == "array" ? owner : auxiliary, false);
                else encoded.GenericInstantiation(TypeReference(typeof(List<>)), 1, false).AddArgument().Type(owner, false);
                LoadType(metadata.AddTypeSpecification(metadata.GetOrAddBlob(signature)));
            }
            else LoadType(shape switch
            {
                "bcl" => TypeReference(typeof(string)), "sibling" => sibling, "nested" => nested,
                "auxiliary" => auxiliary, _ => owner,
            });
        }
        var inspection = api == "ToString" ? typeof(Type).GetMethod(nameof(ToString), Type.EmptyTypes)!
            : typeof(Type).GetProperty(api)!.GetMethod!;
        void LoadInspection()
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(inspection));
            Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
            instructions.OpCode(ILOpCode.Castclass);
            instructions.Token(TypeReference(typeof(MethodInfo)));
        }
        if (dispatch == "token")
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(inspection));
            instructions.OpCode(ILOpCode.Pop);
            LoadString("token");
        }
        else if (dispatch == "lookalike") instructions.Call(MetadataTokens.MethodDefinitionHandle(4));
        else if (dispatch == "invoke")
        {
            LoadInspection();
            LoadReceiver();
            instructions.OpCode(ILOpCode.Ldnull);
            Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
            instructions.OpCode(ILOpCode.Castclass);
            instructions.Token(TypeReference(typeof(string)));
        }
        else if (dispatch == "property")
        {
            LoadType(TypeReference(typeof(Type)));
            LoadString(api);
            Call(typeof(Type).GetMethod(nameof(Type.GetProperty), [typeof(string)])!);
            LoadReceiver();
            instructions.OpCode(ILOpCode.Ldnull);
            Call(typeof(PropertyInfo).GetMethod(nameof(PropertyInfo.GetValue), [typeof(object), typeof(object[])])!);
            instructions.OpCode(ILOpCode.Castclass);
            instructions.Token(TypeReference(typeof(string)));
        }
        else if (dispatch is "delegate" or "named" or "pointer")
        {
            if (dispatch == "delegate") LoadInspection();
            if (dispatch != "pointer") LoadType(SignatureType(typeof(Func<string>)));
            LoadReceiver();
            if (dispatch == "pointer")
            {
                instructions.OpCode(ILOpCode.Dup);
                instructions.OpCode(ILOpCode.Ldvirtftn);
                instructions.Token(MethodReference(inspection));
                instructions.OpCode(ILOpCode.Newobj);
                instructions.Token(MethodReference(typeof(Func<string>).GetConstructors().Single()));
            }
            else if (dispatch == "named")
            {
                LoadString(inspection.Name);
                Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(object), typeof(string)])!);
            }
            else Call(typeof(MethodInfo).GetMethod(nameof(MethodInfo.CreateDelegate), [typeof(Type), typeof(object)])!);
            instructions.OpCode(ILOpCode.Castclass);
            instructions.Token(SignatureType(typeof(Func<string>)));
            Call(typeof(Func<string>).GetMethod(nameof(Func<string>.Invoke))!);
        }
        else if (dispatch == "invoke-member")
        {
            LoadType(TypeReference(typeof(Type)));
            LoadString(api);
            instructions.LoadConstantI4((int)(BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty));
            instructions.OpCode(ILOpCode.Ldnull);
            LoadReceiver();
            instructions.OpCode(ILOpCode.Ldnull);
            Call(typeof(Type).GetMethod(nameof(Type.InvokeMember),
                [typeof(string), typeof(BindingFlags), typeof(Binder), typeof(object), typeof(object[])])!);
            instructions.OpCode(ILOpCode.Castclass);
            instructions.Token(TypeReference(typeof(string)));
        }
        else
        {
            LoadReceiver();
            Call(dispatch == "object" ? typeof(object).GetMethod(nameof(ToString))!
                : shape == "member" ? typeof(MemberInfo).GetProperty(nameof(MemberInfo.Name))!.GetMethod! : inspection);
        }
        instructions.OpCode(ILOpCode.Ret);
        Method("Query", typeof(string), instructions);
        instructions = new InstructionEncoder(new BlobBuilder());
        instructions.LoadConstantI4(42);
        instructions.OpCode(ILOpCode.Ret);
        Method("Probe", typeof(int), instructions);
        instructions = new InstructionEncoder(new BlobBuilder());
        LoadString("lookalike");
        instructions.OpCode(ILOpCode.Ret);
        Method("get_FullName", typeof(string), instructions);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return (image.ToArray(), name, expected);
    }

    private static string? Expected(string shape, string api, string dispatch, string identity)
    {
        if (dispatch is "lookalike" or "token") return dispatch;
        if (shape == "member") return "Probe";
        if (shape == "ordinary") return "42";
        if (shape == "null" || shape == "parameter" && api is "FullName" or "AssemblyQualifiedName") return null;
        if (shape == "parameter") return "T";
        var simple = shape switch
        {
            "bcl" => "String", "sibling" => "Sibling", "nested" => "Nested", "auxiliary" => "Auxiliary",
            "array" => "Owner[]", "auxiliary-array" => "Auxiliary[]", "constructed" => "List`1", _ => "Owner",
        };
        if (api == "Name") return simple;
        if (api == "Namespace") return shape == "bcl" ? "System" : shape == "constructed" ? "System.Collections.Generic" : "TypeNames";
        var full = shape == "bcl" ? "System.String" : shape == "nested" ? "TypeNames.Owner+Nested" : "TypeNames." + simple;
        if (shape == "constructed") full = api == "ToString" ? "System.Collections.Generic.List`1[TypeNames.Owner]"
            : "System.Collections.Generic.List`1[[TypeNames.Owner, " + identity + "]]";
        if (api == "AssemblyQualifiedName") full += ", " + (shape is "bcl" or "constructed" ? typeof(string).Assembly.FullName : identity);
        return full;
    }
}
