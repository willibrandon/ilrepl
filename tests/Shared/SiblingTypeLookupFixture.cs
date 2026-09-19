using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits sibling types in one real assembly with no token dependency from the selected owner to its reflected targets.
/// </summary>
public static class SiblingTypeLookupFixture
{
    /// <summary>
    /// Creates a method that discovers a sibling solely by name and reads its initialized field or constructed property.
    /// </summary>
    /// <param name="api">type, assembly, module, assembly-create, activator, or activator-from.</param>
    /// <param name="shape">plain, nested, generic, array, bounded, matrix, or component.</param>
    /// <param name="arity">The actual BCL overload's parameter count.</param>
    /// <param name="ignoreCase">Whether the lookup uses a lower-case name with case-insensitive matching.</param>
    /// <param name="internalType">Whether the top-level sibling is internal.</param>
    /// <param name="qualified">Whether a type or activation assembly argument includes its full source identity.</param>
    /// <param name="flow">literal, local, return, argument, identity, concat, or runtime.</param>
    /// <param name="path">The assembly file used by CreateInstanceFrom.</param>
    /// <returns>An independent executable PE image whose original Read method returns 42.</returns>
    public static byte[] Create(
        string api,
        string shape,
        int arity,
        bool ignoreCase = false,
        bool internalType = false,
        bool qualified = false,
        string flow = "literal",
        string? path = null)
    {
        var metadata = new MetadataBuilder();
        var assemblyName = "SiblingLookup" + Guid.NewGuid().ToString("N");
        var identity = assemblyName + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        metadata.AddModule(0, metadata.GetOrAddString(assemblyName), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(assemblyName), new Version(1, 0, 0, 0), default, default, 0,
            AssemblyHashAlgorithm.None);
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
                var name = type.Assembly.GetName();
                reference = metadata.AddAssemblyReference(metadata.GetOrAddString(name.Name!), name.Version!, default,
                    metadata.GetOrAddBlob(name.GetPublicKeyToken()!), 0, default);
                references.Add(type.Assembly, reference);
            }

            handle = metadata.AddTypeReference(reference, metadata.GetOrAddString(type.Namespace ?? ""),
                metadata.GetOrAddString(type.Name));
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
            else if (type.IsArray)
            {
                EncodeType(encoder.SZArray(), type.GetElementType()!);
            }
            else
            {
                encoder.Type(TypeReference(type), type.IsValueType);
            }
        }

        EntityHandle MethodReference(MethodBase method)
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

        var nestedTypes = new List<(TypeDefinitionHandle Nested, TypeDefinitionHandle Parent)>();
        var bodies = new BlobBuilder();
        var encoder = new MethodBodyStreamEncoder(bodies);
        TypeDefinitionHandle AddType(string name, TypeAttributes attributes, string space = "Lookup") =>
            metadata.AddTypeDefinition(attributes, metadata.GetOrAddString(space), metadata.GetOrAddString(name),
                TypeReference(typeof(object)), MetadataTokens.FieldDefinitionHandle(metadata.GetRowCount(TableIndex.Field) + 1),
                MetadataTokens.MethodDefinitionHandle(metadata.GetRowCount(TableIndex.MethodDef) + 1));
        MethodDefinitionHandle AddMethod(
            string name,
            byte[] signature,
            InstructionEncoder code,
            MethodAttributes attributes,
            bool locals = false)
        {
            var localSignature = locals ? metadata.AddStandaloneSignature(metadata.GetOrAddBlob(new byte[] { 7, 2, 14, 28 })) : default;
            return metadata.AddMethodDefinition(attributes, MethodImplAttributes.IL, metadata.GetOrAddString(name),
                metadata.GetOrAddBlob(signature), encoder.AddMethodBody(code, maxStack: 16, localVariablesSignature: localSignature),
                MetadataTokens.ParameterHandle(1));
        }

        void Call(InstructionEncoder code, MethodBase method)
        {
            code.OpCode(method.IsStatic || method is ConstructorInfo ? ILOpCode.Call : ILOpCode.Callvirt);
            code.Token(MethodReference(method));
        }

        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        AddType("Owner", TypeAttributes.Public);
        var target = shape switch
        {
            "nested" => "Lookup.Sibling+Nested", "generic" => "Lookup.GenericSibling`1[[" + typeof(int).AssemblyQualifiedName + "]]",
            "array" => "Lookup.Sibling[]", "bounded" => "Lookup.Sibling[*]", "matrix" => "Lookup.Sibling[,]",
            "component" => "System.Collections.Generic.List`1[Lookup.Sibling[]]", _ => "Lookup.Sibling",
        };

        if (ignoreCase)
        {
            target = target.ToLowerInvariant();
        }

        if (qualified && api == "type")
        {
            target = shape == "component"
                ? "System.Collections.Generic.List`1[[" + (ignoreCase ? "lookup.sibling[]" : "Lookup.Sibling[]")
                    + ", " + identity + "]], " + typeof(List<>).Assembly.FullName
                : target + ", " + identity;
        }

        var activating = api is "assembly-create" or "activator" or "activator-from";
        var owner = api switch
        {
            "type" => typeof(Type), "module" => typeof(Module),
            "activator" or "activator-from" => typeof(Activator), _ => typeof(Assembly),
        };

        var methodName = api switch
        {
            "assembly-create" or "activator" => "CreateInstance", "activator-from" => "CreateInstanceFrom", _ => "GetType",
        };

        var lookup = owner.GetMethods().Single(method => method.Name == methodName && method.GetParameters().Length == arity
            && method.GetParameters()[0].ParameterType == typeof(string)
            && (api != "type" || method.GetParameters().Skip(1).All(parameter => parameter.ParameterType == typeof(bool))));
        void LoadName(InstructionEncoder code, bool argument)
        {
            if (argument)
            {
                code.LoadArgument(0);
            }
            else if (flow == "return")
            {
                code.Call(MetadataTokens.MethodDefinitionHandle(2));
            }
            else if (flow == "concat")
            {
                code.LoadString(metadata.GetOrAddUserString(target[..7]));
                code.LoadString(metadata.GetOrAddUserString(target[7..]));
                Call(code, typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!);
            }
            else
            {
                code.LoadString(metadata.GetOrAddUserString(target));
                if (flow == "local")
                {
                    code.StoreLocal(0);
                    code.LoadLocal(0);
                }

                if (flow == "identity")
                {
                    code.Call(MetadataTokens.MethodDefinitionHandle(2));
                }
            }
        }

        void Lookup(InstructionEncoder code, bool argument)
        {
            if (api is "assembly" or "module" or "assembly-create")
            {
                Call(code, typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
                if (api == "module")
                {
                    Call(code, typeof(Assembly).GetProperty(nameof(Assembly.ManifestModule))!.GetMethod!);
                }
            }

            if (api is "activator" or "activator-from")
            {
                code.LoadString(metadata.GetOrAddUserString(api == "activator-from" ? path! : qualified ? identity : assemblyName));
            }

            LoadName(code, argument);
            foreach (var parameter in lookup.GetParameters().Skip(api is "activator" or "activator-from" ? 2 : 1))
            {
                if (parameter.ParameterType == typeof(bool))
                {
                    code.LoadConstantI4(parameter.Name == "ignoreCase" ? ignoreCase ? 1 : 0 : 1);
                }
                else if (parameter.ParameterType == typeof(BindingFlags))
                {
                    code.LoadConstantI4((int)(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.CreateInstance));
                }
                else
                {
                    code.OpCode(ILOpCode.Ldnull);
                }
            }

            Call(code, lookup);
            if (activating)
            {
                if (api is "activator" or "activator-from")
                {
                    Call(code, lookup.ReturnType.GetMethod("Unwrap")!);
                }

                code.StoreLocal(1);
                code.LoadLocal(1);
                Call(code, typeof(object).GetMethod(nameof(object.GetType))!);
                code.LoadString(metadata.GetOrAddUserString("Value"));
                Call(code, typeof(Type).GetMethod(nameof(Type.GetProperty), [typeof(string)])!);
                code.LoadLocal(1);
                Call(code, typeof(PropertyInfo).GetMethod(nameof(PropertyInfo.GetValue), [typeof(object)])!);
            }
            else
            {
                if (shape == "component")
                {
                    Call(code, typeof(Type).GetMethod(nameof(Type.GetGenericArguments))!);
                    code.LoadConstantI4(0);
                    code.OpCode(ILOpCode.Ldelem_ref);
                }

                if (shape is "array" or "bounded" or "matrix" or "component")
                {
                    Call(code, typeof(Type).GetMethod(nameof(Type.GetElementType))!);
                }

                code.LoadString(metadata.GetOrAddUserString("State"));
                Call(code, typeof(Type).GetMethod(nameof(Type.GetField), [typeof(string)])!);
                code.OpCode(ILOpCode.Ldnull);
                Call(code, typeof(FieldInfo).GetMethod(nameof(FieldInfo.GetValue))!);
            }

            code.OpCode(ILOpCode.Unbox_any);
            code.Token(TypeReference(typeof(int)));
            code.OpCode(ILOpCode.Ret);
        }

        var read = new InstructionEncoder(new BlobBuilder());
        if (flow == "argument")
        {
            read.LoadString(metadata.GetOrAddUserString(target));
            read.Call(MetadataTokens.MethodDefinitionHandle(2));
            read.OpCode(ILOpCode.Ret);
        }
        else
        {
            Lookup(read, flow == "runtime");
        }

        AddMethod("Read", flow == "runtime" ? [0, 1, 8, 14] : [0, 0, 8], read,
            MethodAttributes.Public | MethodAttributes.Static, locals: true);
        if (flow is "return" or "argument" or "identity")
        {
            var helper = new InstructionEncoder(new BlobBuilder());
            if (flow == "argument")
            {
                Lookup(helper, true);
            }
            else
            {
                if (flow == "return")
                {
                    helper.LoadString(metadata.GetOrAddUserString(target));
                }
                else
                {
                    helper.LoadArgument(0);
                }

                helper.OpCode(ILOpCode.Ret);
            }

            AddMethod("NameHelper", flow switch { "return" => [0, 0, 14], "argument" => [0, 1, 8, 14], _ => [0, 1, 14, 14] },
                helper, MethodAttributes.Private | MethodAttributes.Static, locals: true);
        }

        TypeDefinitionHandle Sibling(string name, TypeAttributes attributes, bool generic = false)
        {
            var type = AddType(name, attributes, name == "Nested" ? "" : "Lookup");
            if (generic)
            {
                metadata.AddGenericParameter(type, GenericParameterAttributes.None, metadata.GetOrAddString("T"), 0);
            }

            EntityHandle state = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static,
                metadata.GetOrAddString("State"), metadata.GetOrAddBlob(new byte[] { 6, 8 }));
            EntityHandle value = metadata.AddFieldDefinition(FieldAttributes.Private, metadata.GetOrAddString("Constructed"),
                metadata.GetOrAddBlob(new byte[] { 6, 8 }));
            if (generic)
            {
                var specification = new BlobBuilder();
                new BlobEncoder(specification).TypeSpecificationSignature().GenericInstantiation(type, 1, false)
                    .AddArgument().GenericTypeParameter(0);
                var closed = metadata.AddTypeSpecification(metadata.GetOrAddBlob(specification));
                var fieldSignature = metadata.GetOrAddBlob(new byte[] { 6, 8 });
                state = metadata.AddMemberReference(closed, metadata.GetOrAddString("State"), fieldSignature);
                value = metadata.AddMemberReference(closed, metadata.GetOrAddString("Constructed"), fieldSignature);
            }

            var constructor = new InstructionEncoder(new BlobBuilder());
            constructor.LoadArgument(0);
            Call(constructor, typeof(object).GetConstructor(Type.EmptyTypes)!);
            constructor.LoadArgument(0);
            constructor.OpCode(ILOpCode.Ldsfld);
            constructor.Token(state);
            constructor.OpCode(ILOpCode.Stfld);
            constructor.Token(value);
            constructor.OpCode(ILOpCode.Ret);
            AddMethod(".ctor", [32, 0, 1], constructor,
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            var initializer = new InstructionEncoder(new BlobBuilder());
            if (generic)
            {
                initializer.LoadConstantI4(42);
            }
            else
            {
                initializer.Call(MetadataTokens.MethodDefinitionHandle(metadata.GetRowCount(TableIndex.MethodDef) + 3));
            }

            initializer.OpCode(ILOpCode.Stsfld);
            initializer.Token(state);
            initializer.OpCode(ILOpCode.Ret);
            AddMethod(".cctor", [0, 0, 1], initializer, MethodAttributes.Private | MethodAttributes.Static
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            var getter = new InstructionEncoder(new BlobBuilder());
            getter.LoadArgument(0);
            getter.OpCode(ILOpCode.Ldfld);
            getter.Token(value);
            getter.OpCode(ILOpCode.Ret);
            var get = AddMethod("get_Value", [32, 0, 8], getter, MethodAttributes.Public | MethodAttributes.SpecialName);
            var property = metadata.AddProperty(PropertyAttributes.None, metadata.GetOrAddString("Value"),
                metadata.GetOrAddBlob(new byte[] { 40, 0, 8 }));
            metadata.AddPropertyMap(type, property);
            metadata.AddMethodSemantics(property, MethodSemanticsAttributes.Getter, get);
            if (!generic)
            {
                var nested = AddType("Initializer", TypeAttributes.NestedPrivate, "");
                nestedTypes.Add((nested, type));
                var constant = new InstructionEncoder(new BlobBuilder());
                constant.LoadConstantI4(42);
                constant.OpCode(ILOpCode.Ret);
                AddMethod("Initial", [0, 0, 8], constant, MethodAttributes.Public | MethodAttributes.Static);
            }

            return type;
        }

        var visibility = internalType ? TypeAttributes.NotPublic : TypeAttributes.Public;
        var sibling = Sibling("Sibling", visibility);
        nestedTypes.Add((Sibling("Nested", TypeAttributes.NestedPublic), sibling));
        Sibling("GenericSibling`1", visibility, generic: true);
        foreach (var (nested, parent) in nestedTypes.OrderBy(pair => MetadataTokens.GetRowNumber(pair.Nested)))
        {
            metadata.AddNestedType(nested, parent);
        }

        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
