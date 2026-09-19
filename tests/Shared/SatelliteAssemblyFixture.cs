using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits a real main assembly and culture-specific satellite without adding an artificial satellite reference to the main image.
/// </summary>
public static class SatelliteAssemblyFixture
{
    /// <summary>
    /// The actual satellite metadata culture and normal probing directory.
    /// </summary>
    public const string Culture = "fr-FR";

    /// <summary>
    /// The nondefault version shared by the main and satellite identities.
    /// </summary>
    public static Version Version { get; } = new(7, 8, 9, 10);

    /// <summary>
    /// The actionable preflight reason for a copied satellite lookup.
    /// </summary>
    public const string Problem = "satellite assembly lookup cannot reproduce the original satellite context";

    /// <summary>
    /// Constructs a main method returning 42 after real satellite resolution and a satellite with an independent payload.
    /// </summary>
    /// <param name="versioned">Whether to pass an explicit satellite version.</param>
    /// <param name="dispatch">The direct, reflection, delegate, token, or lookalike call form.</param>
    /// <returns>The main image, culture-specific satellite image and unique main assembly name.</returns>
    public static (byte[] Source, byte[] Satellite, string Name) Create(bool versioned, string dispatch)
    {
        var name = "SatelliteSource" + Guid.NewGuid().ToString("N");
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), Version, default, default, 0, AssemblyHashAlgorithm.None);
        var references = new Dictionary<Assembly, AssemblyReferenceHandle>();
        var types = new Dictionary<Type, TypeReferenceHandle>();
        TypeReferenceHandle TypeReference(Type type)
        {
            if (types.TryGetValue(type, out var existing))
            {
                return existing;
            }

            if (!references.TryGetValue(type.Assembly, out var assembly))
            {
                var identity = type.Assembly.GetName();
                assembly = metadata.AddAssemblyReference(metadata.GetOrAddString(identity.Name!), identity.Version!, default,
                    metadata.GetOrAddBlob(identity.GetPublicKeyToken()!), 0, default);
                references.Add(type.Assembly, assembly);
            }

            var result = metadata.AddTypeReference(assembly, metadata.GetOrAddString(type.Namespace ?? ""),
                metadata.GetOrAddString(type.Name));
            types.Add(type, result);
            return result;
        }

        void EncodeType(SignatureTypeEncoder encoder, Type type)
        {
            if (type == typeof(int))
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
            else if (type.IsArray)
            {
                EncodeType(encoder.SZArray(), type.GetElementType()!);
            }
            else if (type.IsConstructedGenericType)
            {
                var arguments = type.GetGenericArguments();
                var signature = encoder.GenericInstantiation(TypeReference(type.GetGenericTypeDefinition()), arguments.Length, false);
                foreach (var argument in arguments)
                {
                    EncodeType(signature.AddArgument(), argument);
                }
            }
            else
            {
                encoder.Type(TypeReference(type), type.IsValueType);
            }
        }

        EntityHandle SignatureType(Type type)
        {
            if (!type.IsConstructedGenericType)
            {
                return TypeReference(type);
            }

            var signature = new BlobBuilder();
            EncodeType(new BlobEncoder(signature).TypeSpecificationSignature(), type);
            return metadata.AddTypeSpecification(metadata.GetOrAddBlob(signature));
        }

        MemberReferenceHandle MethodReference(MethodBase method)
        {
            var signature = new BlobBuilder();
            var parameters = method.GetParameters();
            new BlobEncoder(signature).MethodSignature(isInstanceMethod: !method.IsStatic).Parameters(parameters.Length,
                result =>
                {
                    if (method is not MethodInfo info || info.ReturnType == typeof(void))
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
            return metadata.AddMemberReference(TypeReference(method.DeclaringType!), metadata.GetOrAddString(method.Name),
                metadata.GetOrAddBlob(signature));
        }

        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("SatelliteInspection"),
            metadata.GetOrAddString("Owner"), TypeReference(typeof(object)),
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var parameters = versioned ? new[] { typeof(CultureInfo), typeof(Version) } : [typeof(CultureInfo)];
        var inspection = typeof(Assembly).GetMethod(nameof(Assembly.GetSatelliteAssembly), parameters)!;
        var instructions = new InstructionEncoder(new BlobBuilder());
        void Call(MethodBase method)
        {
            instructions.OpCode(method.IsStatic ? ILOpCode.Call : ILOpCode.Callvirt);
            instructions.Token(MethodReference(method));
        }

        void Receiver() => Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
        void Argument(int index)
        {
            if (index == 0)
            {
                instructions.LoadString(metadata.GetOrAddUserString(Culture));
                Call(typeof(CultureInfo).GetMethod(nameof(CultureInfo.GetCultureInfo), [typeof(string)])!);
            }
            else
            {
                foreach (var part in new[] { Version.Major, Version.Minor, Version.Build, Version.Revision })
                {
                    instructions.LoadConstantI4(part);
                }

                instructions.OpCode(ILOpCode.Newobj);
                instructions.Token(MethodReference(typeof(Version).GetConstructor([typeof(int), typeof(int), typeof(int), typeof(int)])!));
            }
        }

        void ArgumentsArray()
        {
            instructions.LoadConstantI4(parameters.Length);
            instructions.OpCode(ILOpCode.Newarr);
            instructions.Token(TypeReference(typeof(object)));
            for (var index = 0; index < parameters.Length; index++)
            {
                instructions.OpCode(ILOpCode.Dup);
                instructions.LoadConstantI4(index);
                Argument(index);
                instructions.OpCode(ILOpCode.Stelem_ref);
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
            if (dispatch == "reflection")
            {
                instructions.OpCode(ILOpCode.Ldtoken);
                instructions.Token(MethodReference(inspection));
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                Receiver();
                ArgumentsArray();
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                instructions.OpCode(ILOpCode.Castclass);
                instructions.Token(TypeReference(typeof(Assembly)));
            }
            else if (dispatch == "delegate")
            {
                var delegateType = versioned ? typeof(Func<CultureInfo, Version, Assembly>) : typeof(Func<CultureInfo, Assembly>);
                instructions.OpCode(ILOpCode.Ldtoken);
                instructions.Token(SignatureType(delegateType));
                Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                Receiver();
                instructions.LoadString(metadata.GetOrAddUserString(inspection.Name));
                Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(object), typeof(string)])!);
                ArgumentsArray();
                Call(typeof(Delegate).GetMethod(nameof(Delegate.DynamicInvoke))!);
                instructions.OpCode(ILOpCode.Castclass);
                instructions.Token(TypeReference(typeof(Assembly)));
            }
            else
            {
                if (dispatch != "lookalike")
                {
                    Receiver();
                }

                for (var index = 0; index < parameters.Length; index++)
                {
                    Argument(index);
                }

                if (dispatch == "lookalike")
                {
                    instructions.Call(MetadataTokens.MethodDefinitionHandle(2));
                }
                else
                {
                    Call(inspection);
                }
            }

            instructions.OpCode(ILOpCode.Ldnull);
            instructions.OpCode(ILOpCode.Cgt_un);
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Mul);
        }

        instructions.OpCode(ILOpCode.Ret);
        var bodies = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(bodies);
        var readOffset = encoder.AddMethodBody(instructions, maxStack: 16);
        byte[] readSignature = [0, 0, 8];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(readSignature), readOffset, MetadataTokens.ParameterHandle(1));
        if (dispatch == "lookalike")
        {
            var helper = new InstructionEncoder(new BlobBuilder());
            helper.Call(MethodReference(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!));
            helper.OpCode(ILOpCode.Ret);
            var helperOffset = encoder.AddMethodBody(helper);
            var signature = new BlobBuilder();
            new BlobEncoder(signature).MethodSignature().Parameters(parameters.Length,
                result => result.Type().Type(TypeReference(typeof(Assembly)), false), arguments =>
                {
                    foreach (var parameter in parameters)
                    {
                        EncodeType(arguments.AddParameter().Type(), parameter);
                    }
                });
            metadata.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString(inspection.Name), metadata.GetOrAddBlob(signature), helperOffset,
                MetadataTokens.ParameterHandle(1));
        }

        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return (image.ToArray(), Satellite(name), name);
    }

    private static byte[] Satellite(string name)
    {
        var metadata = new MetadataBuilder();
        name += ".resources";
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), Version, metadata.GetOrAddString(Culture), default, 0,
            AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var resources = new BlobBuilder();
        resources.WriteInt32(3);
        byte[] payload = [42, 17, 255];
        resources.WriteBytes(payload);
        metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString("Satellite.payload"), default, 0);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder(), managedResources: resources, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
