using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits real comparisons between executing metadata and a sibling type from the same original source assembly.
/// </summary>
public static class AssemblyReferenceFixture
{
    /// <summary>
    /// Explains why regenerated metadata references cannot retain the original reference identity.
    /// </summary>
    public const string Problem = "assembly and module reference inspection cannot preserve the original identity";

    /// <summary>
    /// Covers distinct equality, hashing, provenance, raw IL, and indirect invocation routes without a redundant Cartesian matrix.
    /// </summary>
    public static IReadOnlyList<(string Target, string Operation, string Flow)> Cases { get; } =
    [
        ("Assembly", "equality", "direct"), ("Module", "equality", "direct"),
        ("Assembly", "inequality", "direct"), ("Module", "inequality", "direct"),
        ("Assembly", "equals", "direct"), ("Module", "equals", "direct"),
        ("Assembly", "hash", "direct"), ("Module", "hash", "direct"),
        ("Assembly", "reference-equals", "direct"), ("Module", "reference-equals", "direct"),
        ("Assembly", "object-equals", "direct"), ("Module", "object-equals", "direct"),
        ("Assembly", "virtual-equals", "direct"), ("Module", "virtual-hash", "direct"),
        ("Assembly", "ceq", "direct"), ("Module", "beq", "direct"), ("Assembly", "bne.un", "direct"),
        ("Module", "ceq", "local"), ("Assembly", "reference-equals", "local"),
        ("Module", "object-equals", "helper-arguments"), ("Assembly", "ceq", "helper-return"),
        ("Assembly", "virtual-equals", "invoke"), ("Module", "virtual-equals", "delegate"),
        ("Assembly", "equals", "named-delegate"),
        ("Assembly", "reference-equals", "invoke"),
        ("ModuleHandle", "equality", "direct"), ("Module", "runtime-hash", "direct"),
        ("Assembly", "reference-equals", "field"),
    ];

    /// <summary>
    /// Ordinary objects and strings, null tests, and method tokens retain executable supported behavior.
    /// </summary>
    public static IReadOnlyList<(string Target, string Operation, string Flow)> SupportedCases { get; } =
    [
        ("Object", "ceq", "direct"), ("Object", "bne.un", "distinct"),
        ("Object", "reference-equals", "distinct"), ("Object", "object-equals", "direct"),
        ("Object", "virtual-equals", "delegate"), ("Object", "virtual-hash", "direct"),
        ("String", "equality", "direct"), ("Assembly", "ceq", "null"),
        ("Module", "reference-equals", "null"), ("Assembly", "beq", "null"),
        ("Assembly", "equality", "token"), ("Module", "hash", "token"),
    ];

    /// <summary>
    /// Maps emitted operations to exact API spellings for diagnostic and dependency assertions.
    /// </summary>
    /// <param name="operation">The emitted reference observation.</param>
    /// <returns>The API name, or null for raw comparison instructions.</returns>
    public static string? Api(string operation) => operation switch
    {
        "equality" => "op_Equality", "inequality" => "op_Inequality",
        "equals" or "object-equals" or "virtual-equals" => "Equals",
        "hash" or "virtual-hash" or "runtime-hash" => "GetHashCode", "reference-equals" => "ReferenceEquals", _ => null,
    };

    /// <summary>
    /// Distinguishes a proven metadata receiver from conservative static reflection with unproven invocation arguments.
    /// </summary>
    /// <param name="operation">The emitted reference observation.</param>
    /// <param name="flow">The invocation route.</param>
    /// <returns>The exact expected preflight explanation.</returns>
    public static string Reason(string operation, string flow) => flow == "invoke" && operation == "reference-equals"
        ? "indirect reflection cannot prove a supported target" : Problem;

    /// <summary>
    /// Creates an independently executable source PE whose selected comparison returns forty-two.
    /// </summary>
    /// <param name="target">The Assembly, Module, ModuleHandle, ordinary Object, or String values being compared.</param>
    /// <param name="operation">The equality, hashing, API, or raw IL operation.</param>
    /// <param name="flow">The direct, local, helper, indirect, token, or supported null/distinct route.</param>
    /// <returns>The complete original source image.</returns>
    public static byte[] Create(string target, string operation, string flow)
    {
        var metadata = new MetadataBuilder();
        var name = "ReferenceSource" + Guid.NewGuid().ToString("N");
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
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("AssemblyReferences"),
            metadata.GetOrAddString("Owner"), TypeReference(typeof(object)),
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var hasHelper = flow is "helper-arguments" or "helper-return";
        var sibling = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("AssemblyReferences"),
            metadata.GetOrAddString("Sibling"), TypeReference(typeof(object)),
            MetadataTokens.FieldDefinitionHandle(flow == "field" ? 3 : 1), MetadataTokens.MethodDefinitionHandle(hasHelper ? 3 : 2));
        if (flow == "field")
        {
            byte[] fieldSignature = [6, 28];
            foreach (var fieldName in new[] { "First", "Second" })
                metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, metadata.GetOrAddString(fieldName),
                    metadata.GetOrAddBlob(fieldSignature));
        }
        var receiver = target switch { "Assembly" => typeof(Assembly), "Module" => typeof(Module),
            "ModuleHandle" => typeof(ModuleHandle), "String" => typeof(string), _ => typeof(object) };
        var method = operation switch
        {
            "equality" => receiver.GetMethod("op_Equality", [receiver, receiver]),
            "inequality" => receiver.GetMethod("op_Inequality", [receiver, receiver]),
            "equals" => receiver.GetMethod(nameof(Equals), [typeof(object)]),
            "hash" => receiver.GetMethod(nameof(GetHashCode), Type.EmptyTypes),
            "reference-equals" => typeof(object).GetMethod(nameof(ReferenceEquals)),
            "object-equals" => typeof(object).GetMethod(nameof(Equals), [typeof(object), typeof(object)]),
            "virtual-equals" => typeof(object).GetMethod(nameof(Equals), [typeof(object)]),
            "virtual-hash" => typeof(object).GetMethod(nameof(GetHashCode), Type.EmptyTypes),
            "runtime-hash" => typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.GetHashCode), [typeof(object)]),
            _ => null,
        };
        var instructions = new InstructionEncoder(new BlobBuilder(), new ControlFlowBuilder());
        void Call(MethodBase called)
        {
            instructions.OpCode(called.IsStatic ? ILOpCode.Call : ILOpCode.Callvirt);
            instructions.Token(MethodReference(called));
        }

        void LoadType(EntityHandle type)
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(type);
            Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        }

        void LoadOriginal(bool first)
        {
            if (!first && flow == "null") instructions.OpCode(ILOpCode.Ldnull);
            else if (target == "Object")
            {
                if (!first && flow != "distinct") instructions.LoadLocal(0);
                else
                {
                    instructions.OpCode(ILOpCode.Newobj);
                    instructions.Token(MethodReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
                    if (first)
                    {
                        instructions.OpCode(ILOpCode.Dup);
                        instructions.StoreLocal(0);
                    }
                }
            }
            else if (target == "String")
            {
                instructions.LoadString(metadata.GetOrAddUserString("same "));
                instructions.LoadString(metadata.GetOrAddUserString("contents"));
                Call(typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!);
            }
            else if (first)
            {
                Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
                if (target is "Module" or "ModuleHandle")
                    Call(typeof(Assembly).GetProperty(nameof(Assembly.ManifestModule))!.GetMethod!);
                if (target == "ModuleHandle") Call(typeof(Module).GetProperty(nameof(Module.ModuleHandle))!.GetMethod!);
            }
            else
            {
                LoadType(sibling);
                Call(target == "Assembly" ? typeof(Type).GetProperty(nameof(Type.Assembly))!.GetMethod!
                    : typeof(MemberInfo).GetProperty(nameof(MemberInfo.Module))!.GetMethod!);
                if (target == "ModuleHandle") Call(typeof(Module).GetProperty(nameof(Module.ModuleHandle))!.GetMethod!);
            }
        }

        void LoadOperand(bool first)
        {
            if (flow == "local") instructions.LoadLocal(first ? 0 : 1);
            else if (flow == "field")
            {
                instructions.OpCode(ILOpCode.Ldsfld);
                instructions.Token(MetadataTokens.FieldDefinitionHandle(first ? 1 : 2));
            }
            else if (!first && flow == "helper-return") instructions.Call(MetadataTokens.MethodDefinitionHandle(2));
            else LoadOriginal(first);
            if (method?.DeclaringType is { } owner && (owner == typeof(Assembly) || owner == typeof(Module)))
            {
                instructions.OpCode(ILOpCode.Castclass);
                instructions.Token(TypeReference(owner));
            }
        }

        void LoadMethod()
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(method!));
            Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
        }

        if (flow == "token")
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(method!));
            instructions.OpCode(ILOpCode.Pop);
            instructions.LoadConstantI4(42);
        }
        else
        {
            if (flow == "local")
            {
                LoadOriginal(first: true);
                instructions.StoreLocal(0);
                LoadOriginal(first: false);
                instructions.StoreLocal(1);
            }
            if (flow == "field")
            {
                LoadOriginal(first: true);
                instructions.OpCode(ILOpCode.Stsfld);
                instructions.Token(MetadataTokens.FieldDefinitionHandle(1));
                LoadOriginal(first: false);
                instructions.OpCode(ILOpCode.Stsfld);
                instructions.Token(MetadataTokens.FieldDefinitionHandle(2));
            }
            if (flow == "invoke")
            {
                LoadMethod();
                if (method!.IsStatic) instructions.OpCode(ILOpCode.Ldnull);
                else LoadOperand(first: true);
                instructions.LoadConstantI4(method.IsStatic ? 2 : 1);
                instructions.OpCode(ILOpCode.Newarr);
                instructions.Token(TypeReference(typeof(object)));
                for (var index = 0; index < (method.IsStatic ? 2 : 1); index++)
                {
                    instructions.OpCode(ILOpCode.Dup);
                    instructions.LoadConstantI4(index);
                    LoadOperand(first: method.IsStatic && index == 0);
                    instructions.OpCode(ILOpCode.Stelem_ref);
                }
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                instructions.OpCode(ILOpCode.Unbox_any);
                instructions.Token(TypeReference(typeof(bool)));
            }
            else if (flow is "delegate" or "named-delegate")
            {
                if (flow == "delegate")
                {
                    LoadMethod();
                    instructions.OpCode(ILOpCode.Castclass);
                    instructions.Token(TypeReference(typeof(MethodInfo)));
                }
                LoadType(SignatureType(typeof(Func<object, bool>)));
                LoadOperand(first: true);
                if (flow == "named-delegate")
                {
                    instructions.LoadString(metadata.GetOrAddUserString(method!.Name));
                    Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(object), typeof(string)])!);
                }
                else Call(typeof(MethodInfo).GetMethod(nameof(MethodInfo.CreateDelegate), [typeof(Type), typeof(object)])!);
                instructions.OpCode(ILOpCode.Castclass);
                instructions.Token(SignatureType(typeof(Func<object, bool>)));
                LoadOperand(first: false);
                Call(typeof(Func<object, bool>).GetMethod(nameof(Func<object, bool>.Invoke))!);
            }
            else
            {
                LoadOperand(first: true);
                if (operation is "hash" or "virtual-hash" or "runtime-hash") Call(method!);
                LoadOperand(first: false);
                if (flow == "helper-arguments") instructions.Call(MetadataTokens.MethodDefinitionHandle(2));
                else if (operation is "hash" or "virtual-hash" or "runtime-hash")
                {
                    Call(method!);
                    instructions.OpCode(ILOpCode.Ceq);
                }
                else if (operation == "ceq") instructions.OpCode(ILOpCode.Ceq);
                else if (operation is "beq" or "bne.un")
                {
                    var matched = instructions.DefineLabel();
                    var done = instructions.DefineLabel();
                    instructions.Branch(operation == "beq" ? ILOpCode.Beq : ILOpCode.Bne_un, matched);
                    instructions.LoadConstantI4(0);
                    instructions.Branch(ILOpCode.Br, done);
                    instructions.MarkLabel(matched);
                    instructions.LoadConstantI4(1);
                    instructions.MarkLabel(done);
                }
                else Call(method!);
            }
            var expected = flow is not ("null" or "distinct");
            if (operation is "inequality" or "bne.un") expected = !expected;
            if (!expected)
            {
                instructions.LoadConstantI4(0);
                instructions.OpCode(ILOpCode.Ceq);
            }
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Mul);
        }
        instructions.OpCode(ILOpCode.Ret);
        var locals = new BlobBuilder();
        var variables = new BlobEncoder(locals).LocalVariableSignature(2);
        variables.AddVariable().Type().Object();
        variables.AddVariable().Type().Object();
        var bodies = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(bodies);
        var offset = encoder.AddMethodBody(instructions, maxStack: 8,
            localVariablesSignature: metadata.AddStandaloneSignature(metadata.GetOrAddBlob(locals)));
        byte[] signature = [0, 0, 8];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(signature), offset, MetadataTokens.ParameterHandle(1));
        if (hasHelper)
        {
            instructions = new InstructionEncoder(new BlobBuilder());
            if (flow == "helper-return") LoadOriginal(first: false);
            else
            {
                instructions.LoadArgument(0);
                instructions.LoadArgument(1);
                Call(method!);
            }
            instructions.OpCode(ILOpCode.Ret);
            byte[] helperSignature = flow == "helper-return" ? [0, 0, 28] : [0, 2, 2, 28, 28];
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString("ObserveSibling"), metadata.GetOrAddBlob(helperSignature), encoder.AddMethodBody(instructions),
                MetadataTokens.ParameterHandle(1));
        }
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
