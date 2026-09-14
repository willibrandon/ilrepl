using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits an unused assembly reference whose observable table disappears from generated method copies.
/// </summary>
public static class ReferenceInspectionFixture
{
    /// <summary>
    /// The absent reference deliberately retained only in the source assembly's reference table.
    /// </summary>
    public const string UnusedReference = "ReferenceInspection.Unused";

    /// <summary>
    /// The expected diagnostic for a known reference-table inspection target.
    /// </summary>
    public const string Problem = "referenced assembly inspection cannot reproduce the original reference table";

    /// <summary>
    /// The executable dispatch forms that cannot preserve the source reference table in a generated copy.
    /// </summary>
    public static IReadOnlyList<string> Dispatches { get; } =
        ["direct", "ldftn", "virtual delegate", "invoke", "named delegate", "runtime named delegate"];

    /// <summary>
    /// Creates a real PE whose table-search method returns 42 only when its unused source reference is present.
    /// </summary>
    /// <param name="dispatch">The actual inspection, method pointer, metadata token, or lookalike operation.</param>
    /// <returns>The independently executable image.</returns>
    public static byte[] Create(string dispatch)
    {
        var metadata = new MetadataBuilder();
        var name = "ReferenceInspection" + Guid.NewGuid().ToString("N");
        metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0,
            AssemblyHashAlgorithm.None);
        metadata.AddAssemblyReference(metadata.GetOrAddString(UnusedReference), new Version(7, 8, 9, 10), default, default, 0, default);
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
            var declaring = definition.DeclaringType!;
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
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("ReferenceInspection"),
            metadata.GetOrAddString("Owner"), TypeReference(typeof(object)),
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var instructions = new InstructionEncoder(new BlobBuilder(), new ControlFlowBuilder());
        var inspection = typeof(Assembly).GetMethod(nameof(Assembly.GetReferencedAssemblies))!;
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

        void LoadAssembly() => Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
        if (dispatch is "ldftn" or "token")
        {
            instructions.OpCode(dispatch == "token" ? ILOpCode.Ldtoken : ILOpCode.Ldftn);
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
            if (dispatch == "direct")
            {
                LoadAssembly();
                Call(inspection);
            }
            else if (dispatch == "invoke")
            {
                LoadType(typeof(Assembly));
                instructions.LoadString(metadata.GetOrAddUserString(nameof(Assembly.GetReferencedAssemblies)));
                Call(typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string)])!);
                LoadAssembly();
                instructions.OpCode(ILOpCode.Ldnull);
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                instructions.OpCode(ILOpCode.Castclass);
                instructions.Token(SignatureType(typeof(AssemblyName[])));
            }
            else
            {
                var delegateType = typeof(Func<AssemblyName[]>);
                if (dispatch == "virtual delegate")
                {
                    LoadAssembly();
                    instructions.OpCode(ILOpCode.Dup);
                    instructions.OpCode(ILOpCode.Ldvirtftn);
                    instructions.Token(MethodReference(inspection));
                    instructions.OpCode(ILOpCode.Newobj);
                    instructions.Token(MethodReference(delegateType.GetConstructors().Single()));
                }
                else
                {
                    LoadType(delegateType);
                    LoadAssembly();
                    if (dispatch == "runtime named delegate") instructions.LoadArgument(0);
                    else instructions.LoadString(metadata.GetOrAddUserString(nameof(Assembly.GetReferencedAssemblies)));
                    Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(object), typeof(string)])!);
                }

                instructions.OpCode(ILOpCode.Ldnull);
                Call(typeof(Delegate).GetMethod(nameof(Delegate.DynamicInvoke))!);
                instructions.OpCode(ILOpCode.Castclass);
                instructions.Token(SignatureType(typeof(AssemblyName[])));
            }

            instructions.StoreLocal(0);
            instructions.LoadConstantI4(0);
            instructions.StoreLocal(1);
            var check = instructions.DefineLabel();
            var next = instructions.DefineLabel();
            var read = instructions.DefineLabel();
            instructions.Branch(ILOpCode.Br, check);
            instructions.MarkLabel(read);
            instructions.LoadLocal(0);
            instructions.LoadLocal(1);
            instructions.OpCode(ILOpCode.Ldelem_ref);
            Call(typeof(AssemblyName).GetProperty(nameof(AssemblyName.Name))!.GetMethod!);
            instructions.LoadString(metadata.GetOrAddUserString(UnusedReference));
            Call(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
            instructions.Branch(ILOpCode.Brfalse, next);
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Ret);
            instructions.MarkLabel(next);
            instructions.LoadLocal(1);
            instructions.LoadConstantI4(1);
            instructions.OpCode(ILOpCode.Add);
            instructions.StoreLocal(1);
            instructions.MarkLabel(check);
            instructions.LoadLocal(1);
            instructions.LoadLocal(0);
            instructions.OpCode(ILOpCode.Ldlen);
            instructions.OpCode(ILOpCode.Conv_i4);
            instructions.Branch(ILOpCode.Blt, read);
            instructions.LoadConstantI4(0);
        }

        instructions.OpCode(ILOpCode.Ret);
        var locals = new BlobBuilder();
        var variables = new BlobEncoder(locals).LocalVariableSignature(2);
        EncodeType(variables.AddVariable().Type(), typeof(AssemblyName[]));
        variables.AddVariable().Type().Int32();
        var bodies = new BlobBuilder();
        var bodyEncoder = new MethodBodyStreamEncoder(bodies);
        var offset = bodyEncoder.AddMethodBody(instructions, maxStack: 5,
            localVariablesSignature: metadata.AddStandaloneSignature(metadata.GetOrAddBlob(locals)));
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
                metadata.GetOrAddString(nameof(Assembly.GetReferencedAssemblies)), metadata.GetOrAddBlob(signature), helperOffset,
                MetadataTokens.ParameterHandle(1));
        }

        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
