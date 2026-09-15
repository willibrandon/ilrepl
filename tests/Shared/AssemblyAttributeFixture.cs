using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits real assembly, module, type, and method attributes inspected through the runtime's actual reflection overloads.
/// </summary>
public static class AssemblyAttributeFixture
{
    /// <summary>
    /// Enumerates the actual attribute inspection overloads for one target and dispatch family.
    /// </summary>
    /// <param name="target">Assembly, Module, Type, or Member.</param>
    /// <param name="dispatch">instance, attribute, extensions, data, or provider.</param>
    /// <returns>The closed executable APIs in a stable signature order.</returns>
    public static IReadOnlyList<MethodInfo> Apis(string target, string dispatch)
    {
        var receiver = target switch { "Assembly" => typeof(Assembly), "Module" => typeof(Module), _ => typeof(MemberInfo) };
        var owner = dispatch switch
        {
            "attribute" => typeof(Attribute), "extensions" => typeof(CustomAttributeExtensions),
            "data" => typeof(CustomAttributeData), "provider" => typeof(ICustomAttributeProvider), _ => receiver,
        };
        return owner.GetMethods(BindingFlags.Public | (dispatch is "instance" or "provider" ? BindingFlags.Instance : BindingFlags.Static))
            .Where(method => method.Name is "GetCustomAttributes" or "GetCustomAttribute" or "IsDefined"
                or "GetCustomAttributesData" or "get_CustomAttributes")
            .Where(method => !method.IsStatic || method.GetParameters().FirstOrDefault()?.ParameterType == receiver)
            .Select(method => method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeof(CLSCompliantAttribute)) : method)
            .OrderBy(method => method.ToString(), StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Creates an attributed executable whose chosen inspection observes one actual attribute and returns 42.
    /// </summary>
    /// <param name="target">The metadata target supplied to the reflection API.</param>
    /// <param name="inspection">The actual closed BCL API to invoke.</param>
    /// <param name="metadataOnly">Whether to inspect its method token without invoking the API.</param>
    /// <returns>The independent portable executable image.</returns>
    public static byte[] Create(string target, MethodInfo inspection, bool metadataOnly = false)
    {
        var metadata = new MetadataBuilder();
        var name = "AttributeInspection" + Guid.NewGuid().ToString("N");
        var module = metadata.AddModule(0, metadata.GetOrAddString(name), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        var assembly = metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, 0,
            AssemblyHashAlgorithm.None);
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
            else if (type.IsArray) EncodeType(encoder.SZArray(), type.GetElementType()!);
            else if (type.IsGenericParameter) encoder.GenericMethodTypeParameter(type.GenericParameterPosition);
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
            var member = metadata.AddMemberReference(TypeReference(definition.DeclaringType!), metadata.GetOrAddString(definition.Name),
                metadata.GetOrAddBlob(signature));
            if (method is not MethodInfo { IsGenericMethod: true } closed) return member;
            var specification = new BlobBuilder();
            var encoded = new BlobEncoder(specification).MethodSpecificationSignature(closed.GetGenericArguments().Length);
            foreach (var argument in closed.GetGenericArguments()) EncodeType(encoded.AddArgument(), argument);
            return metadata.AddMethodSpecification(member, metadata.GetOrAddBlob(specification));
        }

        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("AttributeInspection"),
            metadata.GetOrAddString("Owner"), TypeReference(typeof(object)),
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var instructions = new InstructionEncoder(new BlobBuilder());
        void Call(MethodBase method)
        {
            instructions.OpCode(method.IsStatic || method is ConstructorInfo ? ILOpCode.Call : ILOpCode.Callvirt);
            instructions.Token(MethodReference(method));
        }

        void LoadType(EntityHandle handle)
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(handle);
            Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        }

        if (metadataOnly)
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(MethodReference(inspection));
            instructions.OpCode(ILOpCode.Pop);
        }
        else
        {
            if (target is "Assembly" or "Module")
            {
                Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
                if (target == "Module") Call(typeof(Assembly).GetProperty(nameof(Assembly.ManifestModule))!.GetMethod!);
            }
            else if (target == "Type") LoadType(owner);
            else
            {
                instructions.OpCode(ILOpCode.Ldtoken);
                instructions.Token(MetadataTokens.MethodDefinitionHandle(1));
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
            }

            foreach (var parameter in inspection.GetParameters().Skip(inspection.IsStatic ? 1 : 0))
            {
                if (parameter.ParameterType == typeof(Type)) LoadType(TypeReference(typeof(CLSCompliantAttribute)));
                else if (parameter.ParameterType == typeof(bool)) instructions.LoadConstantI4(0);
                else throw new InvalidOperationException("unsupported inspection argument " + parameter.ParameterType);
            }

            Call(inspection);
            var result = inspection.ReturnType;
            if (result.IsArray)
            {
                instructions.OpCode(ILOpCode.Ldlen);
                instructions.OpCode(ILOpCode.Conv_i4);
            }
            else if (typeof(Attribute).IsAssignableFrom(result))
            {
                instructions.OpCode(ILOpCode.Ldnull);
                instructions.OpCode(ILOpCode.Cgt_un);
            }
            else if (result != typeof(bool))
            {
                var element = result.GetGenericArguments().Single();
                Call(typeof(Enumerable).GetMethods().Single(method => method.Name == nameof(Enumerable.Count)
                    && method.GetParameters().Length == 1).MakeGenericMethod(element));
            }
        }

        instructions.LoadConstantI4(42);
        if (!metadataOnly) instructions.OpCode(ILOpCode.Mul);
        instructions.OpCode(ILOpCode.Ret);
        var bodies = new BlobBuilder();
        var offset = new MethodBodyStreamEncoder(bodies).AddMethodBody(instructions);
        byte[] readSignature = [0, 0, 8];
        var read = metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(readSignature), offset, MetadataTokens.ParameterHandle(1));
        var constructor = MethodReference(typeof(CLSCompliantAttribute).GetConstructor([typeof(bool)])!);
        byte[] attribute = [1, 0, 1, 0, 0];
        foreach (var entity in new EntityHandle[] { assembly, module, owner, read })
        {
            metadata.AddCustomAttribute(entity, constructor, metadata.GetOrAddBlob(attribute));
        }

        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
