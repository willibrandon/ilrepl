using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits real external inspectors that distinguish source metadata from regenerated copied metadata.
/// </summary>
public static class MetadataBoundaryFixture
{
    private static readonly byte[] ObjectFieldSignature = [6, 28];
    private static readonly byte[] ObjectLocalSignature = [7, 1, 28];
    private static readonly byte[] Int32FieldSignature = [6, 8];
    private static readonly byte[] ObjectArrayFieldSignature = [6, 29, 28];

    /// <summary>
    /// Covers copied metadata carried through typed, erased, stored, helper and complete-array arguments.
    /// </summary>
    public static IReadOnlyList<(string Target, string Flow, string Origin)> RejectedCases { get; } =
    [
        ("Assembly", "direct", "copied"), ("Module", "direct", "copied"), ("ModuleHandle", "boxed", "copied"),
        ("Assembly", "cast", "copied"), ("Module", "local", "copied"), ("Assembly", "field", "copied"),
        ("Module", "helper", "copied"), ("Assembly", "array", "copied"), ("Module", "array", "copied"),
        ("ModuleHandle", "array", "copied"), ("Type", "direct", "copied"), ("Method", "direct", "copied"),
        ("Assembly", "constructor", "copied"), ("Assembly", "external-field", "copied"),
        ("Assembly", "calli", "copied"), ("Assembly", "invoke", "copied"), ("Assembly", "delegate", "copied"),
        ("Assembly", "set-value", "copied"), ("Assembly", "activate", "copied"),
        ("Assembly", "address", "copied"), ("Assembly", "helper-store", "copied"),
        ("Assembly", "external-address", "copied"), ("Assembly", "external-array", "copied"),
        ("Assembly", "get-assembly", "copied"),
        ("Assembly", "callback-ctor", "copied"), ("Module", "callback-ctor", "copied"),
        ("ModuleHandle", "callback-ctor", "copied"), ("Assembly", "callback-methodinfo", "copied"),
        ("Assembly", "callback-factory", "copied"), ("Assembly", "callback-helper", "copied"),
        ("Module", "callback-return-array", "copied"), ("Assembly", "callback-return-wrapper", "copied"),
        ("Assembly", "callback-ref", "copied"), ("Assembly", "callback-carrier-array", "copied"),
        ("Assembly", "callback-instance", "copied"), ("Assembly", "callback-pointer", "copied"),
        ("Assembly", "callback-handle-pointer", "copied"),
        ("CurrentMethod", "callback-ctor", "copied"),
    ];

    /// <summary>
    /// Covers unchanged BCL and public sibling metadata, ordinary objects, null, and complete arrays of ordinary objects.
    /// </summary>
    public static IReadOnlyList<(string Target, string Flow, string Origin)> SupportedCases { get; } =
    [
        ("Assembly", "direct", "bcl"), ("Module", "direct", "bcl"), ("ModuleHandle", "boxed", "bcl"),
        ("Type", "direct", "bcl"), ("Method", "direct", "bcl"),
        ("Assembly", "direct", "sibling"), ("Module", "local", "sibling"),
        ("Type", "direct", "sibling"), ("Method", "direct", "sibling"),
        ("Object", "direct", "object"), ("Object", "direct", "null"), ("Object", "array", "object"),
        ("Object", "constructor", "object"), ("Object", "external-field", "object"),
        ("Assembly", "calli", "bcl"), ("Assembly", "invoke", "bcl"), ("Assembly", "delegate", "bcl"),
        ("Object", "set-value", "object"), ("Object", "activate", "object"),
        ("Object", "address", "object"), ("Object", "helper-store", "object"),
        ("Object", "external-address", "object"), ("Object", "external-array", "object"),
        ("Assembly", "get-assembly", "sibling"),
        ("Assembly", "callback-ctor", "bcl"), ("Module", "callback-ctor", "sibling"),
        ("ModuleHandle", "callback-ctor", "bcl"), ("Assembly", "callback-methodinfo", "bcl"),
        ("Assembly", "callback-factory", "sibling"), ("Object", "callback-helper", "object"),
        ("Object", "callback-return-array", "object"), ("Object", "callback-return-wrapper", "object"),
        ("Object", "callback-ref", "object"), ("Object", "callback-carrier-array", "object"),
        ("Object", "callback-instance", "object"), ("Assembly", "callback-pointer", "bcl"),
        ("Object", "callback-handle-pointer", "object"), ("Assembly", "callback-local", "copied"),
        ("Method", "callback-ctor", "bcl"), ("Method", "callback-ctor", "sibling"),
    ];

    /// <summary>
    /// Distinguishes unchanged external values and metadata consumed solely by a local copied callback invocation.
    /// </summary>
    /// <param name="flow">The transport or callback route.</param>
    /// <param name="origin">The original metadata or ordinary value producer.</param>
    /// <returns>Whether the fixture retains compatible behavior after copying.</returns>
    public static bool IsSupported(string flow, string origin) => origin != "copied" || flow == "callback-local";

    /// <summary>
    /// Names the actual call, constructor, field, or reflection transport responsible for the external boundary.
    /// </summary>
    /// <param name="flow">The route to the external inspector.</param>
    /// <returns>The specific sink text expected in its dependency report.</returns>
    public static string Sink(string flow) => flow switch
    {
        "constructor" => "Inspector::.ctor", "external-field" or "external-address" => "Saved",
        "external-array" => "array store", "calli" => "indirect call",
        "set-value" => "FieldInfo::SetValue", "activate" => "Activator::CreateInstance", _ => "Inspector::Inspect",
    };

    /// <summary>
    /// Identifies the unchanged external method referenced by a supported source body.
    /// </summary>
    /// <param name="flow">The argument transport or storage route.</param>
    /// <returns>The inspector method name retained in the copied caller.</returns>
    public static string CalledMethod(string flow) => flow switch
    {
        "constructor" or "activate" => "ReadValue",
        "external-field" or "set-value" or "external-address" or "external-array" => "ReadSaved", _ => "Inspect",
    };

    /// <summary>
    /// Creates two assemblies whose public inspector returns forty-two only when its argument retains the actual source identity.
    /// </summary>
    /// <param name="target">The assembly, module, module handle, type, method, or ordinary object argument.</param>
    /// <param name="flow">The direct, cast, local, field, copied helper, boxed, or whole-array route.</param>
    /// <param name="origin">The copied owner, unchanged sibling, BCL, ordinary object, or literal null producer.</param>
    /// <returns>The real source and inspector images with their source assembly name.</returns>
    public static (byte[] Source, byte[] Inspector, string SourceName) Create(string target, string flow, string origin)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var source = "MetadataBoundarySource" + suffix;
        var inspector = "MetadataBoundaryInspector" + suffix;
        return (Build(source, inspector, target, flow, origin, false), Build(inspector, source, target, flow, origin, true), source);
    }

    private static byte[] Build(string name, string otherName, string target, string flow, string origin, bool inspector)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 2, 3, 4), default, default, 0, AssemblyHashAlgorithm.None);
        var assemblies = new Dictionary<Assembly, AssemblyReferenceHandle>();
        var types = new Dictionary<Type, TypeReferenceHandle>();
        TypeReferenceHandle TypeReference(Type type)
        {
            if (types.TryGetValue(type, out var handle))
            {
                return handle;
            }

            if (!assemblies.TryGetValue(type.Assembly, out var assembly))
            {
                var identity = type.Assembly.GetName();
                assembly = metadata.AddAssemblyReference(metadata.GetOrAddString(identity.Name!), identity.Version!, default,
                    metadata.GetOrAddBlob(identity.GetPublicKeyToken()!), 0, default);
                assemblies.Add(type.Assembly, assembly);
            }

            handle = metadata.AddTypeReference(assembly, metadata.GetOrAddString(type.Namespace!), metadata.GetOrAddString(type.Name));
            types.Add(type, handle);
            return handle;
        }

        void Encode(SignatureTypeEncoder encoder, Type type)
        {
            if (type == typeof(int))
            {
                encoder.Int32();
            }
            else if (type == typeof(bool))
            {
                encoder.Boolean();
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
                Encode(encoder.SZArray(), type.GetElementType()!);
            }
            else
            {
                encoder.Type(TypeReference(type), type.IsValueType);
            }
        }

        BlobHandle Signature(Type result, Type[] parameters, bool instance = false)
        {
            var blob = new BlobBuilder();
            new BlobEncoder(blob).MethodSignature(isInstanceMethod: instance).Parameters(parameters.Length, returned =>
            {
                if (result == typeof(void))
                {
                    returned.Void();
                }
                else
                {
                    Encode(returned.Type(), result);
                }
            }, arguments =>
            {
                foreach (var parameter in parameters)
                {
                    Encode(arguments.AddParameter().Type(isByRef: parameter.IsByRef),
                        parameter.IsByRef ? parameter.GetElementType()! : parameter);
                }
            });

            return metadata.GetOrAddBlob(blob);
        }

        MemberReferenceHandle MethodReference(MethodBase method) => metadata.AddMemberReference(TypeReference(method.DeclaringType!),
            metadata.GetOrAddString(method.Name), Signature(method is MethodInfo info ? info.ReturnType : typeof(void),
                method.GetParameters().Select(parameter => parameter.ParameterType).ToArray(), !method.IsStatic));
        void Call(InstructionEncoder code, MethodBase method)
        {
            code.OpCode(method.IsStatic ? ILOpCode.Call : ILOpCode.Callvirt);
            code.Token(MethodReference(method));
        }

        var counterpart = metadata.AddAssemblyReference(metadata.GetOrAddString(otherName), new Version(1, 2, 3, 4),
            default, default, 0, default);
        var callback = flow.StartsWith("callback-", StringComparison.Ordinal);
        var callbackPointer = flow is "callback-pointer" or "callback-handle-pointer";
        var callbackResult = flow == "callback-ref" ? typeof(void) : typeof(object);
        var callbackArguments = flow == "callback-ref" ? new[] { typeof(object).MakeByRefType() } : Type.EmptyTypes;
        var callbackSignature = Signature(callbackResult, callbackArguments, true);
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("MetadataBoundary"),
            metadata.GetOrAddString(inspector ? "Inspector" : "Owner"), TypeReference(typeof(object)),
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var sink = flow is "constructor" or "external-field" or "calli" or "invoke" or "delegate" or "set-value" or "activate"
            or "external-address" or "external-array";
        var fieldSignature = metadata.GetOrAddBlob(ObjectFieldSignature);
        if (inspector && sink)
        {
            metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed, metadata.GetOrAddString("MetadataBoundary"),
                metadata.GetOrAddString("Probe"), TypeReference(typeof(MulticastDelegate)),
                MetadataTokens.FieldDefinitionHandle(flow == "external-array" ? 4 : 3),
                MetadataTokens.MethodDefinitionHandle(flow == "external-array" ? 6 : 5));
            metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static, metadata.GetOrAddString("Saved"), fieldSignature);
            metadata.AddFieldDefinition(FieldAttributes.Private, metadata.GetOrAddString("_result"),
                metadata.GetOrAddBlob(Int32FieldSignature));
            if (flow == "external-array")
            {
                metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static,
                    metadata.GetOrAddString("Items"), metadata.GetOrAddBlob(ObjectArrayFieldSignature));
            }
        }
        else if (inspector && callback)
        {
            metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Sealed, metadata.GetOrAddString("MetadataBoundary"),
                metadata.GetOrAddString("Probe"), TypeReference(typeof(MulticastDelegate)),
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(2));
        }

        EntityHandle nominal;
        EntityHandle read;
        if (inspector)
        {
            nominal = metadata.AddTypeReference(counterpart, metadata.GetOrAddString("MetadataBoundary"),
                metadata.GetOrAddString(origin == "sibling" ? "Sibling" : "Owner"));
            read = metadata.AddMemberReference(nominal, metadata.GetOrAddString(origin == "sibling" ? "Ping" : "Read"),
                Signature(typeof(int), Type.EmptyTypes));
        }
        else
        {
            var sibling = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("MetadataBoundary"),
                metadata.GetOrAddString("Sibling"), TypeReference(typeof(object)),
                MetadataTokens.FieldDefinitionHandle(callback ? 3 : 2), MetadataTokens.MethodDefinitionHandle(callback ? 5 : 3));
            metadata.AddFieldDefinition(FieldAttributes.Private | FieldAttributes.Static, metadata.GetOrAddString("Saved"),
                metadata.GetOrAddBlob(ObjectFieldSignature));
            if (callback)
            {
                metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static,
                    metadata.GetOrAddString("Calls"), metadata.GetOrAddBlob(Int32FieldSignature));
            }

            nominal = origin == "sibling" ? sibling : owner;
            read = MetadataTokens.MethodDefinitionHandle(origin == "sibling" ? callback ? 5 : 3 : 1);
            if (flow == "callback-return-wrapper")
            {
                metadata.AddTypeDefinition(TypeAttributes.NotPublic, metadata.GetOrAddString("MetadataBoundary"),
                    metadata.GetOrAddString("Holder"), TypeReference(typeof(object)),
                    MetadataTokens.FieldDefinitionHandle(3), MetadataTokens.MethodDefinitionHandle(6));
                metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("Value"), fieldSignature);
            }
        }

        void Value(InstructionEncoder code, bool source)
        {
            if (origin == "null")
            {
                code.OpCode(ILOpCode.Ldnull);
            }
            else if (origin == "object")
            {
                code.OpCode(ILOpCode.Newobj);
                code.Token(MethodReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            }
            else if (target == "CurrentMethod")
            {
                if (source)
                {
                    Call(code, typeof(MethodBase).GetMethod(nameof(MethodBase.GetCurrentMethod))!);
                }
                else
                {
                    code.OpCode(ILOpCode.Ldtoken);
                    code.Token(nominal);
                    Call(code, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                    code.LoadString(metadata.GetOrAddUserString("Metadata"));
                    code.LoadConstantI4((int)(BindingFlags.Static | BindingFlags.NonPublic));
                    Call(code, typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string), typeof(BindingFlags)])!);
                }
            }
            else if (target == "Method")
            {
                code.OpCode(ILOpCode.Ldtoken);
                code.Token(origin == "bcl" ? MethodReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!) : read);
                Call(code, typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
            }
            else if (source && origin == "copied" && target == "Assembly" && flow != "get-assembly")
            {
                Call(code, typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
            }
            else
            {
                code.OpCode(ILOpCode.Ldtoken);
                code.Token(origin == "bcl" ? TypeReference(typeof(string)) : nominal);
                Call(code, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
                if (target == "Assembly")
                {
                    Call(code, source && flow == "get-assembly"
                    ? typeof(Assembly).GetMethod(nameof(Assembly.GetAssembly), [typeof(Type)])!
                    : typeof(Type).GetProperty(nameof(Type.Assembly))!.GetMethod!);
                }

                if (target is "Module" or "ModuleHandle")
                {
                    Call(code, typeof(Type).GetProperty(nameof(Type.Module))!.GetMethod!);
                }

                if (target == "ModuleHandle")
                {
                    Call(code, typeof(Module).GetProperty(nameof(Module.ModuleHandle))!.GetMethod!);
                    code.OpCode(ILOpCode.Box);
                    code.Token(TypeReference(typeof(ModuleHandle)));
                }
            }
        }

        var argumentType = callbackPointer ? typeof(nint) : flow == "array" ? typeof(object[])
            : flow != "direct" ? typeof(object) : target switch
        {
            "Assembly" => typeof(Assembly), "Module" => typeof(Module),
            "Type" or "Method" => typeof(MemberInfo), _ => typeof(object),
        };

        var inspectSignature = Signature(typeof(int), [argumentType]);
        var bodies = new MethodBodyStreamEncoder(new BlobBuilder());
        var code = new InstructionEncoder(new BlobBuilder());
        if (inspector)
        {
            code.LoadArgument(0);
            if (callback && flow != "callback-local")
            {
                if (callbackPointer)
                {
                    code.OpCode(ILOpCode.Calli);
                    code.Token(metadata.AddStandaloneSignature(Signature(typeof(object), Type.EmptyTypes)));
                }
                else
                {
                    if (flow == "callback-carrier-array")
                    {
                        code.OpCode(ILOpCode.Castclass);
                        code.Token(TypeReference(typeof(Array)));
                        code.LoadConstantI4(1);
                        Call(code, typeof(Array).GetMethod(nameof(Array.GetValue), [typeof(int)])!);
                    }

                    code.OpCode(ILOpCode.Castclass);
                    code.Token(MetadataTokens.TypeDefinitionHandle(3));
                    if (flow == "callback-ref")
                    {
                        code.LoadLocalAddress(0);
                    }

                    code.OpCode(ILOpCode.Callvirt);
                    code.Token(metadata.AddMemberReference(MetadataTokens.TypeDefinitionHandle(3),
                        metadata.GetOrAddString("Invoke"), callbackSignature));
                    if (flow == "callback-ref")
                    {
                        code.LoadLocal(0);
                    }
                }

                if (flow == "callback-return-array")
                {
                    code.OpCode(ILOpCode.Castclass);
                    code.Token(TypeReference(typeof(Array)));
                    code.LoadConstantI4(1);
                    Call(code, typeof(Array).GetMethod(nameof(Array.GetValue), [typeof(int)])!);
                }
                else if (flow == "callback-return-wrapper")
                {
                    code.StoreLocal(0);
                    code.LoadLocal(0);
                    Call(code, typeof(object).GetMethod(nameof(GetType))!);
                    code.LoadString(metadata.GetOrAddUserString("Value"));
                    Call(code, typeof(Type).GetMethod(nameof(Type.GetField), [typeof(string)])!);
                    code.LoadLocal(0);
                    Call(code, typeof(FieldInfo).GetMethod(nameof(FieldInfo.GetValue), [typeof(object)])!);
                }
            }

            if (flow == "array")
            {
                code.LoadConstantI4(1);
                code.OpCode(ILOpCode.Ldelem_ref);
            }

            if (flow == "callback-local")
            {
                code.OpCode(ILOpCode.Ldnull);
            }
            else if (origin == "object")
            {
                Call(code, typeof(object).GetMethod(nameof(GetType))!);
                code.OpCode(ILOpCode.Ldtoken);
                code.Token(TypeReference(typeof(object)));
                Call(code, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
            }
            else
            {
                Value(code, false);
            }

            Call(code, typeof(object).GetMethod(nameof(Equals), [typeof(object), typeof(object)])!);
            code.LoadConstantI4(42);
            code.OpCode(ILOpCode.Mul);
            code.OpCode(ILOpCode.Ret);
            var inspectorLocals = callback ? metadata.AddStandaloneSignature(metadata.GetOrAddBlob(ObjectLocalSignature)) : default;
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString("Inspect"), inspectSignature, bodies.AddMethodBody(code, localVariablesSignature: inspectorLocals),
                MetadataTokens.ParameterHandle(1));
            if (sink)
            {
                var constructor = new InstructionEncoder(new BlobBuilder());
                constructor.LoadArgument(0);
                constructor.Call(MethodReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
                constructor.LoadArgument(0);
                constructor.LoadArgument(1);
                constructor.Call(MetadataTokens.MethodDefinitionHandle(1));
                constructor.OpCode(ILOpCode.Stfld);
                constructor.Token(MetadataTokens.FieldDefinitionHandle(2));
                constructor.OpCode(ILOpCode.Ret);
                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.IL, metadata.GetOrAddString(".ctor"), Signature(typeof(void), [typeof(object)], true),
                    bodies.AddMethodBody(constructor), MetadataTokens.ParameterHandle(1));
                var readValue = new InstructionEncoder(new BlobBuilder());
                readValue.LoadArgument(0);
                readValue.OpCode(ILOpCode.Ldfld);
                readValue.Token(MetadataTokens.FieldDefinitionHandle(2));
                readValue.OpCode(ILOpCode.Ret);
                metadata.AddMethodDefinition(MethodAttributes.Public, MethodImplAttributes.IL, metadata.GetOrAddString("ReadValue"),
                    Signature(typeof(int), Type.EmptyTypes, true), bodies.AddMethodBody(readValue), MetadataTokens.ParameterHandle(1));
                var readSaved = new InstructionEncoder(new BlobBuilder());
                readSaved.OpCode(ILOpCode.Ldsfld);
                readSaved.Token(MetadataTokens.FieldDefinitionHandle(flow == "external-array" ? 3 : 1));
                if (flow == "external-array")
                {
                    readSaved.LoadConstantI4(1);
                    readSaved.OpCode(ILOpCode.Ldelem_ref);
                }

                readSaved.Call(MetadataTokens.MethodDefinitionHandle(1));
                readSaved.OpCode(ILOpCode.Ret);
                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                    metadata.GetOrAddString("ReadSaved"), Signature(typeof(int), Type.EmptyTypes), bodies.AddMethodBody(readSaved),
                    MetadataTokens.ParameterHandle(1));
                if (flow == "external-array")
                {
                    var initializer = new InstructionEncoder(new BlobBuilder());
                    initializer.LoadConstantI4(2);
                    initializer.OpCode(ILOpCode.Newarr);
                    initializer.Token(TypeReference(typeof(object)));
                    initializer.OpCode(ILOpCode.Stsfld);
                    initializer.Token(MetadataTokens.FieldDefinitionHandle(3));
                    initializer.OpCode(ILOpCode.Ret);
                    metadata.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName
                        | MethodAttributes.RTSpecialName, MethodImplAttributes.IL, metadata.GetOrAddString(".cctor"),
                        Signature(typeof(void), Type.EmptyTypes), bodies.AddMethodBody(initializer), MetadataTokens.ParameterHandle(1));
                }

                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.Runtime, metadata.GetOrAddString(".ctor"),
                    Signature(typeof(void), [typeof(object), typeof(nint)], true), -1, MetadataTokens.ParameterHandle(1));
                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                    MethodImplAttributes.Runtime, metadata.GetOrAddString("Invoke"), Signature(typeof(int), Type.EmptyTypes, true),
                    -1, MetadataTokens.ParameterHandle(1));
            }
            else if (callback)
            {
                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.Runtime, metadata.GetOrAddString(".ctor"),
                    Signature(typeof(void), [typeof(object), typeof(nint)], true), -1, MetadataTokens.ParameterHandle(1));
                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                    MethodImplAttributes.Runtime, metadata.GetOrAddString("Invoke"), callbackSignature,
                    -1, MetadataTokens.ParameterHandle(1));
            }
        }
        else
        {
            var external = metadata.AddTypeReference(counterpart, metadata.GetOrAddString("MetadataBoundary"),
                metadata.GetOrAddString("Inspector"));
            var inspect = metadata.AddMemberReference(external, metadata.GetOrAddString("Inspect"), inspectSignature);
            void LoadType(EntityHandle type)
            {
                code.OpCode(ILOpCode.Ldtoken);
                code.Token(type);
                Call(code, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
            }

            void LoadInspect()
            {
                code.OpCode(ILOpCode.Ldtoken);
                code.Token(inspect);
                Call(code, typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                code.OpCode(ILOpCode.Castclass);
                code.Token(TypeReference(typeof(MethodInfo)));
            }

            void Arguments()
            {
                code.LoadConstantI4(1);
                code.OpCode(ILOpCode.Newarr);
                code.Token(TypeReference(typeof(object)));
                code.OpCode(ILOpCode.Dup);
                code.LoadConstantI4(0);
                Value(code, true);
                code.OpCode(ILOpCode.Stelem_ref);
            }

            void ReadValue()
            {
                code.OpCode(ILOpCode.Callvirt);
                code.Token(metadata.AddMemberReference(external, metadata.GetOrAddString("ReadValue"),
                    Signature(typeof(int), Type.EmptyTypes, true)));
            }

            void LoadCallbackMethod()
            {
                code.OpCode(ILOpCode.Ldtoken);
                code.Token(MetadataTokens.MethodDefinitionHandle(2));
                Call(code, typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                code.OpCode(ILOpCode.Castclass);
                code.Token(TypeReference(typeof(MethodInfo)));
            }

            void EmitCallback()
            {
                var probe = metadata.AddTypeReference(counterpart, metadata.GetOrAddString("MetadataBoundary"),
                    metadata.GetOrAddString("Probe"));
                if (callbackPointer)
                {
                    if (flow == "callback-handle-pointer")
                    {
                        LoadCallbackMethod();
                        Call(code, typeof(MethodBase).GetProperty(nameof(MethodBase.MethodHandle))!.GetMethod!);
                        code.StoreLocal(0);
                        code.LoadLocalAddress(0);
                        code.Call(MethodReference(typeof(RuntimeMethodHandle).GetMethod(nameof(RuntimeMethodHandle.GetFunctionPointer))!));
                    }
                    else
                    {
                        code.OpCode(ILOpCode.Ldftn);
                        code.Token(MetadataTokens.MethodDefinitionHandle(2));
                    }
                }
                else
                {
                    if (flow == "callback-carrier-array")
                    {
                        code.LoadConstantI4(2);
                        code.OpCode(ILOpCode.Newarr);
                        code.Token(TypeReference(typeof(object)));
                        code.OpCode(ILOpCode.Dup);
                        code.LoadConstantI4(1);
                    }

                    if (flow == "callback-methodinfo")
                    {
                        LoadCallbackMethod();
                        LoadType(probe);
                        Call(code, typeof(MethodInfo).GetMethod(nameof(MethodInfo.CreateDelegate), [typeof(Type)])!);
                        code.OpCode(ILOpCode.Castclass);
                        code.Token(probe);
                    }
                    else if (flow == "callback-factory")
                    {
                        LoadType(probe);
                        LoadCallbackMethod();
                        Call(code, typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), [typeof(Type), typeof(MethodInfo)])!);
                        code.OpCode(ILOpCode.Castclass);
                        code.Token(probe);
                    }
                    else
                    {
                        if (flow == "callback-instance")
                        {
                            code.OpCode(ILOpCode.Newobj);
                            code.Token(MetadataTokens.MethodDefinitionHandle(4));
                        }
                        else
                        {
                            code.OpCode(ILOpCode.Ldnull);
                        }

                        code.OpCode(ILOpCode.Ldftn);
                        code.Token(MetadataTokens.MethodDefinitionHandle(2));
                        code.OpCode(ILOpCode.Newobj);
                        code.Token(metadata.AddMemberReference(probe, metadata.GetOrAddString(".ctor"),
                            Signature(typeof(void), [typeof(object), typeof(nint)], true)));
                    }

                    if (flow == "callback-carrier-array")
                    {
                        code.OpCode(ILOpCode.Stelem_ref);
                    }

                    if (flow == "callback-local")
                    {
                        code.OpCode(ILOpCode.Callvirt);
                        code.Token(metadata.AddMemberReference(probe, metadata.GetOrAddString("Invoke"), callbackSignature));
                        code.OpCode(ILOpCode.Pop);
                        code.OpCode(ILOpCode.Ldnull);
                    }
                }

                code.Call(inspect);
            }

            void EmitSink()
            {
                if (flow == "constructor")
                {
                    Value(code, true);
                    code.OpCode(ILOpCode.Newobj);
                    code.Token(metadata.AddMemberReference(external, metadata.GetOrAddString(".ctor"),
                        Signature(typeof(void), [typeof(object)], true)));
                    ReadValue();
                }
                else if (flow is "external-field" or "set-value" or "external-address" or "external-array")
                {
                    var saved = metadata.AddMemberReference(external, metadata.GetOrAddString("Saved"), fieldSignature);
                    if (flow == "set-value")
                    {
                        code.OpCode(ILOpCode.Ldtoken);
                        code.Token(saved);
                        Call(code, typeof(FieldInfo).GetMethod(nameof(FieldInfo.GetFieldFromHandle), [typeof(RuntimeFieldHandle)])!);
                        code.OpCode(ILOpCode.Ldnull);
                    }
                    else if (flow == "external-address")
                    {
                        code.OpCode(ILOpCode.Ldsflda);
                        code.Token(saved);
                    }
                    else if (flow == "external-array")
                    {
                        code.OpCode(ILOpCode.Ldsfld);
                        code.Token(metadata.AddMemberReference(external, metadata.GetOrAddString("Items"),
                            metadata.GetOrAddBlob(ObjectArrayFieldSignature)));
                        code.LoadConstantI4(1);
                    }

                    Value(code, true);
                    if (flow == "set-value")
                    {
                        Call(code, typeof(FieldInfo).GetMethod(nameof(FieldInfo.SetValue),
                            [typeof(object), typeof(object)])!);
                    }
                    else if (flow == "external-address")
                    {
                        code.OpCode(ILOpCode.Stind_ref);
                    }
                    else if (flow == "external-array")
                    {
                        code.OpCode(ILOpCode.Stelem_ref);
                    }
                    else
                    {
                        code.OpCode(ILOpCode.Stsfld);
                        code.Token(saved);
                    }

                    code.Call(metadata.AddMemberReference(external, metadata.GetOrAddString("ReadSaved"),
                        Signature(typeof(int), Type.EmptyTypes)));
                }
                else if (flow == "calli")
                {
                    Value(code, true);
                    code.OpCode(ILOpCode.Ldftn);
                    code.Token(inspect);
                    code.OpCode(ILOpCode.Calli);
                    code.Token(metadata.AddStandaloneSignature(inspectSignature));
                }
                else if (flow == "invoke")
                {
                    LoadInspect();
                    code.OpCode(ILOpCode.Ldnull);
                    Arguments();
                    Call(code, typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), [typeof(object), typeof(object[])])!);
                    code.OpCode(ILOpCode.Unbox_any);
                    code.Token(TypeReference(typeof(int)));
                }
                else if (flow == "delegate")
                {
                    var probe = metadata.AddTypeReference(counterpart, metadata.GetOrAddString("MetadataBoundary"),
                        metadata.GetOrAddString("Probe"));
                    LoadType(probe);
                    Value(code, true);
                    LoadInspect();
                    Call(code, typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate),
                        [typeof(Type), typeof(object), typeof(MethodInfo)])!);
                    code.OpCode(ILOpCode.Castclass);
                    code.Token(probe);
                    code.OpCode(ILOpCode.Callvirt);
                    code.Token(metadata.AddMemberReference(probe, metadata.GetOrAddString("Invoke"),
                        Signature(typeof(int), Type.EmptyTypes, true)));
                }
                else if (flow == "activate")
                {
                    LoadType(external);
                    Arguments();
                    Call(code, typeof(Activator).GetMethod(nameof(Activator.CreateInstance), [typeof(Type), typeof(object[])])!);
                    code.OpCode(ILOpCode.Castclass);
                    code.Token(external);
                    ReadValue();
                }
            }

            if (callback)
            {
                EmitCallback();
            }
            else if (sink)
            {
                EmitSink();
            }
            else
            {
                if (flow == "array")
                {
                    code.LoadConstantI4(2);
                    code.OpCode(ILOpCode.Newarr);
                    code.Token(TypeReference(typeof(object)));
                    code.OpCode(ILOpCode.Dup);
                    code.LoadConstantI4(0);
                    code.LoadString(metadata.GetOrAddUserString("ordinary first slot"));
                    code.OpCode(ILOpCode.Stelem_ref);
                    code.OpCode(ILOpCode.Dup);
                    code.LoadConstantI4(1);
                }

                if (flow is "address" or "helper-store")
                {
                    code.LoadLocalAddress(0);
                }

                if (flow is "helper" or "helper-store")
                {
                    code.Call(MetadataTokens.MethodDefinitionHandle(2));
                }
                else
                {
                    Value(code, true);
                }

                if (flow == "address")
                {
                    code.OpCode(ILOpCode.Stind_ref);
                }

                if (flow is "address" or "helper-store")
                {
                    code.LoadLocal(0);
                }

                if (flow == "cast")
                {
                    code.OpCode(ILOpCode.Castclass);
                    code.Token(TypeReference(typeof(object)));
                }

                if (flow == "local")
                {
                    code.StoreLocal(0);
                    code.LoadLocal(0);
                }

                if (flow == "field")
                {
                    code.OpCode(ILOpCode.Stsfld);
                    code.Token(MetadataTokens.FieldDefinitionHandle(1));
                    code.OpCode(ILOpCode.Ldsfld);
                    code.Token(MetadataTokens.FieldDefinitionHandle(1));
                }

                if (flow == "array")
                {
                    code.OpCode(ILOpCode.Stelem_ref);
                }

                code.Call(inspect);
            }

            code.OpCode(ILOpCode.Ret);
            var localSignature = metadata.GetOrAddBlob(ObjectLocalSignature);
            if (flow == "callback-handle-pointer")
            {
                var locals = new BlobBuilder();
                new BlobEncoder(locals).LocalVariableSignature(1).AddVariable().Type()
                    .Type(TypeReference(typeof(RuntimeMethodHandle)), true);
                localSignature = metadata.GetOrAddBlob(locals);
            }

            var local = metadata.AddStandaloneSignature(localSignature);
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString("Read"), Signature(typeof(int), Type.EmptyTypes),
                bodies.AddMethodBody(code, localVariablesSignature: local), MetadataTokens.ParameterHandle(1));
            var relay = new InstructionEncoder(new BlobBuilder());
            if (callback)
            {
                relay.OpCode(ILOpCode.Ldsfld);
                relay.Token(MetadataTokens.FieldDefinitionHandle(2));
                relay.LoadConstantI4(1);
                relay.OpCode(ILOpCode.Add);
                relay.OpCode(ILOpCode.Stsfld);
                relay.Token(MetadataTokens.FieldDefinitionHandle(2));
            }

            if (flow == "callback-return-array")
            {
                relay.LoadConstantI4(2);
                relay.OpCode(ILOpCode.Newarr);
                relay.Token(TypeReference(typeof(object)));
                relay.OpCode(ILOpCode.Dup);
                relay.LoadConstantI4(1);
                Value(relay, true);
                relay.OpCode(ILOpCode.Stelem_ref);
            }
            else if (flow == "callback-return-wrapper")
            {
                relay.OpCode(ILOpCode.Newobj);
                relay.Token(MetadataTokens.MethodDefinitionHandle(6));
                relay.OpCode(ILOpCode.Dup);
                Value(relay, true);
                relay.OpCode(ILOpCode.Stfld);
                relay.Token(MetadataTokens.FieldDefinitionHandle(3));
            }
            else if (flow is "helper-store" or "callback-ref")
            {
                relay.LoadArgument(0);
                Value(relay, true);
                relay.OpCode(ILOpCode.Stind_ref);
            }
            else if (flow == "callback-helper")
            {
                relay.Call(MetadataTokens.MethodDefinitionHandle(3));
            }
            else if (flow == "helper" || callback)
            {
                Value(relay, true);
            }
            else
            {
                relay.OpCode(ILOpCode.Ldnull);
            }

            relay.OpCode(ILOpCode.Ret);
            metadata.AddMethodDefinition(MethodAttributes.Private | (flow == "callback-instance" ? 0 : MethodAttributes.Static),
                MethodImplAttributes.IL, metadata.GetOrAddString("Metadata"), flow is "helper-store" or "callback-ref"
                    ? Signature(typeof(void), [typeof(object).MakeByRefType()])
                    : Signature(typeof(object), Type.EmptyTypes, flow == "callback-instance"),
                bodies.AddMethodBody(relay),
                MetadataTokens.ParameterHandle(1));
            if (callback)
            {
                var helper = new InstructionEncoder(new BlobBuilder());
                if (flow == "callback-helper")
                {
                    Value(helper, true);
                }
                else
                {
                    helper.OpCode(ILOpCode.Ldnull);
                }

                helper.OpCode(ILOpCode.Ret);
                metadata.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static, MethodImplAttributes.IL,
                    metadata.GetOrAddString("ValueHelper"), Signature(typeof(object), Type.EmptyTypes), bodies.AddMethodBody(helper),
                    MetadataTokens.ParameterHandle(1));
                var constructor = new InstructionEncoder(new BlobBuilder());
                constructor.LoadArgument(0);
                constructor.Call(MethodReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
                constructor.OpCode(ILOpCode.Ret);
                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.IL, metadata.GetOrAddString(".ctor"), Signature(typeof(void), Type.EmptyTypes, true),
                    bodies.AddMethodBody(constructor), MetadataTokens.ParameterHandle(1));
            }

            var ping = new InstructionEncoder(new BlobBuilder());
            ping.LoadConstantI4(42);
            ping.OpCode(ILOpCode.Ret);
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString("Ping"), Signature(typeof(int), Type.EmptyTypes), bodies.AddMethodBody(ping),
                MetadataTokens.ParameterHandle(1));
            if (flow == "callback-return-wrapper")
            {
                var constructor = new InstructionEncoder(new BlobBuilder());
                constructor.LoadArgument(0);
                constructor.Call(MethodReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
                constructor.OpCode(ILOpCode.Ret);
                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.IL, metadata.GetOrAddString(".ctor"), Signature(typeof(void), Type.EmptyTypes, true),
                    bodies.AddMethodBody(constructor), MetadataTokens.ParameterHandle(1));
            }
        }

        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies.Builder, flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();
    }
}
