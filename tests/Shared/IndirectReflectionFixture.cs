using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace IlRepl.Tests.Shared;

/// <summary>
/// Emits actual reflection dispatches against assembly metadata that copied method contexts cannot reproduce.
/// </summary>
public static class IndirectReflectionFixture
{
    /// <summary>
    /// Enumerates representative unsupported targets and all supported indirect-dispatch overload shapes.
    /// </summary>
    public static IEnumerable<(string Target, string Api, string Dispatch)> Cases
    {
        get
        {
            foreach (var target in new[] { "Assembly", "Module" })
            {
                foreach (var dispatch in new[] { "invoke", "invoke options", "local", "helper", "handle", "invoker", "delegate",
                    "method delegate",
                    "delegate options", "open delegate", "open delegate options", "open method delegate", "generic method delegate",
                    "open generic method delegate", "named delegate", "named delegate ignore case", "named delegate options",
                    "invoke member", "invoke member culture", "invoke member options", "ireflect" })
                    yield return (target, "GetTypes", dispatch);
                foreach (var api in new[] { "GetCustomAttributes", "GetCustomAttributesData", "IsDefined" })
                    yield return (target, api, "invoke");
                foreach (var dispatch in new[] { "property", "property index", "property options" })
                    yield return (target, "CustomAttributes", dispatch);
            }

            foreach (var api in new[] { "GetExportedTypes", "GetManifestResourceNames", "GetManifestResourceInfo",
                "GetManifestResourceStream", "typed resource", "typed attributes" })
                yield return ("Assembly", api, "invoke");
            foreach (var api in new[] { "DefinedTypes", "ExportedTypes" })
                yield return ("Assembly", api, "property");
            yield return ("Assembly", "GetManifestResourceNames", "handle");
            yield return ("Assembly", "GetManifestResourceNames", "invoker");
            yield return ("Assembly", "GetManifestResourceNames", "delegate");
            yield return ("Assembly", "GetCustomAttributesData", "method delegate");
            yield return ("Assembly", "GetCustomAttributesData", "invoke member");
            yield return ("Assembly", "GetTypes", "starg");
            yield return ("Assembly", "GetTypes", "function pointer");
            foreach (var target in new[] { "Type", "Assembly", "Module" }) yield return (target, "GetType", "invoke");
            foreach (var target in new[] { "Assembly", "Activator" }) yield return (target, "CreateInstance", "invoke");
        }
    }

    /// <summary>
    /// Selects the precise diagnostic category expected for one unsupported target.
    /// </summary>
    /// <param name="api">The inspected API or property.</param>
    /// <returns>The specific preflight explanation.</returns>
    public static string Problem(string api) => api is "GetType" or "CreateInstance"
        ? "indirect type lookup cannot translate copied names"
        : api.Contains("Resource", StringComparison.Ordinal) || api == "typed resource"
        ? "original assembly's resources"
        : api.Contains("Attributes", StringComparison.Ordinal) || api is "IsDefined" or "typed attributes"
            ? "assembly and module attribute inspection cannot reproduce the original metadata"
            : "assembly and module type enumeration cannot reproduce";

    /// <summary>
    /// Exercises additional indirect overloads against private members that exist in the copied owner context.
    /// </summary>
    /// <param name="dispatch">The supported invocation overload or factory.</param>
    /// <returns>The complete source with a private target returning 42.</returns>
    public static string SupportedSource(string dispatch)
    {
        if (dispatch == "runtime delegate") return """
            .class public Owner {
              .method public instance void .ctor() {
                ldarg.0
                call instance void Object::.ctor()
                ret
              }
              .method public instance int32 Hidden() {
                ldc.i4.s 42
                ret
              }
              .method public static int32 Read(object target) {
                ldtoken class Func<int32>
                call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)
                ldarg.0
                ldstr "Hidden"
                call class Delegate Delegate::CreateDelegate(class Type, object, string)
                ldnull
                callvirt instance object Delegate::DynamicInvoke(object[])
                unbox.any int32
                ret
              }
            }
            """;
        if (dispatch.StartsWith("property", StringComparison.Ordinal))
        {
            var arguments = dispatch == "property index" ? "ldnull\n" : "ldc.i4.0\nldnull\nldnull\nldnull\n";
            var parameters = dispatch == "property index" ? "object, object[]"
                : "object, valuetype BindingFlags, class Binder, object[], class CultureInfo";
            return ReflectionDependencyExamples.Source("property").Replace(
                "callvirt instance object PropertyInfo::GetValue(object)",
                arguments + "callvirt instance object PropertyInfo::GetValue(" + parameters + ")", StringComparison.Ordinal);
        }

        const string invocation = "ldnull\nldnull\ncallvirt instance object MethodBase::Invoke(object, object[])\nunbox.any int32\n";
        var replacement = dispatch switch
        {
            "invoke options" => "ldnull\nldc.i4.0\nldnull\nldnull\nldnull\n"
                + "callvirt instance object MethodBase::Invoke(object, valuetype BindingFlags, class Binder, object[], class CultureInfo)\n"
                + "unbox.any int32\n",
            "invoker" => "call class MethodInvoker MethodInvoker::Create(class MethodBase)\nldnull\n"
                + "callvirt instance object MethodInvoker::Invoke(object)\nunbox.any int32\n",
            "function pointer" => "callvirt instance valuetype RuntimeMethodHandle MethodBase::get_MethodHandle()\n"
                + "stloc.0\nldloca.s 0\ncall instance native int RuntimeMethodHandle::GetFunctionPointer()\ncalli int32()\n",
            _ => "ldtoken class Func<int32>\ncall class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle)\n"
                + "callvirt instance class Delegate MethodInfo::CreateDelegate(class Type)\ncastclass class Func<int32>\n"
                + "callvirt instance !0 class Func<int32>::Invoke()\n",
        };
        var source = ReflectionDependencyExamples.Source("method").Replace(invocation, replacement, StringComparison.Ordinal);
        return dispatch == "function pointer" ? source.Replace(".method public static int32 Read() {",
            ".method public static int32 Read() {\n.locals init (valuetype RuntimeMethodHandle pointer)",
            StringComparison.Ordinal) : source;
    }

    /// <summary>
    /// Creates one attributed type and embedded payload whose selected indirect inspection returns exactly 42.
    /// </summary>
    /// <param name="target">The Assembly, Module, Type, or Activator API owner.</param>
    /// <param name="api">The inspected API or property.</param>
    /// <param name="dispatch">The reflection invocation or binding operation.</param>
    /// <returns>A complete independently executable portable image.</returns>
    public static byte[] Create(string target, string api, string dispatch)
    {
        var metadata = new MetadataBuilder();
        var name = "IndirectReflection" + Guid.NewGuid().ToString("N");
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
        var owner = metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("IndirectReflection"),
            metadata.GetOrAddString("Owner"), TypeReference(typeof(object)),
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var instructions = new InstructionEncoder(new BlobBuilder());
        var receiver = target switch
        {
            "Assembly" => typeof(Assembly), "Type" => typeof(Type), "Activator" => typeof(Activator), _ => typeof(Module),
        };
        var property = dispatch.StartsWith("property", StringComparison.Ordinal);
        var apiName = api switch
        {
            "typed resource" => "GetManifestResourceStream", "typed attributes" => "GetCustomAttributes", _ => api,
        };
        var parameterTypes = api switch
        {
            "GetManifestResourceInfo" or "GetManifestResourceStream" => new[] { typeof(string) },
            "typed resource" => [typeof(Type), typeof(string)],
            "GetCustomAttributes" => [typeof(bool)],
            "IsDefined" or "typed attributes" => [typeof(Type), typeof(bool)],
            "GetType" => [typeof(string)],
            "CreateInstance" when target == "Activator" => [typeof(string), typeof(string)],
            "CreateInstance" => [typeof(string)],
            _ => Type.EmptyTypes,
        };
        var inspection = property ? receiver.GetProperty(apiName)!.GetMethod! : receiver.GetMethod(apiName, parameterTypes)!;
        void Call(MethodBase method)
        {
            instructions.OpCode(method.IsStatic ? ILOpCode.Call : ILOpCode.Callvirt);
            instructions.Token(MethodReference(method));
        }

        void LoadType(EntityHandle handle)
        {
            instructions.OpCode(ILOpCode.Ldtoken);
            instructions.Token(handle);
            Call(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        }

        void LoadReceiver()
        {
            if (inspection.IsStatic)
            {
                instructions.OpCode(ILOpCode.Ldnull);
                return;
            }

            Call(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly))!);
            if (target == "Module") Call(typeof(Assembly).GetProperty(nameof(Assembly.ManifestModule))!.GetMethod!);
        }

        void LoadName(string? value = null)
        {
            if (value is not null) instructions.LoadString(metadata.GetOrAddUserString(value));
            else if (dispatch == "unknown") instructions.LoadArgument(0);
            else instructions.LoadString(metadata.GetOrAddUserString(dispatch == "starg" ? "ToString" : apiName));
        }

        void LoadMethod(string? nameOverride = null)
        {
            if (dispatch == "handle")
            {
                instructions.OpCode(ILOpCode.Ldtoken);
                instructions.Token(MethodReference(inspection));
                Call(typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle), [typeof(RuntimeMethodHandle)])!);
                return;
            }

            if (dispatch == "custom resolver")
            {
                instructions.LoadString(metadata.GetOrAddUserString("Benign.Name"));
                instructions.OpCode(ILOpCode.Ldnull);
                instructions.OpCode(ILOpCode.Ldnull);
                instructions.OpCode(ILOpCode.Ldftn);
                instructions.Token(MetadataTokens.MethodDefinitionHandle(2));
                instructions.OpCode(ILOpCode.Newobj);
                instructions.Token(MethodReference(typeof(Func<Assembly, string, bool, Type>).GetConstructors().Single()));
                Call(typeof(Type).GetMethod(nameof(Type.GetType),
                    [typeof(string), typeof(Func<AssemblyName, Assembly>), typeof(Func<Assembly, string, bool, Type>)])!);
            }
            else LoadType(TypeReference(receiver));
            LoadName(nameOverride);
            if (dispatch is "helper" or "starg") instructions.Call(MetadataTokens.MethodDefinitionHandle(2));
            else if (parameterTypes.Length == 0)
                Call(typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string)])!);
            else
            {
                instructions.LoadConstantI4(parameterTypes.Length);
                instructions.OpCode(ILOpCode.Newarr);
                instructions.Token(TypeReference(typeof(Type)));
                for (var index = 0; index < parameterTypes.Length; index++)
                {
                    instructions.OpCode(ILOpCode.Dup);
                    instructions.LoadConstantI4(index);
                    LoadType(TypeReference(parameterTypes[index]));
                    instructions.OpCode(ILOpCode.Stelem_ref);
                }

                Call(typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string), typeof(Type[])])!);
            }
        }

        void LoadArguments()
        {
            if (parameterTypes.Length == 0)
            {
                instructions.OpCode(ILOpCode.Ldnull);
                return;
            }

            instructions.LoadConstantI4(parameterTypes.Length);
            instructions.OpCode(ILOpCode.Newarr);
            instructions.Token(TypeReference(typeof(object)));
            for (var index = 0; index < parameterTypes.Length; index++)
            {
                instructions.OpCode(ILOpCode.Dup);
                instructions.LoadConstantI4(index);
                var parameter = parameterTypes[index];
                if (parameter == typeof(Type)) LoadType(api == "typed resource" ? owner : TypeReference(typeof(CLSCompliantAttribute)));
                else if (parameter == typeof(string))
                {
                    var value = api switch
                    {
                        "GetType" when target == "Type" => "System.Int32",
                        "GetType" or "CreateInstance" when target != "Activator" => "IndirectReflection.Owner",
                        "CreateInstance" when index == 0 => typeof(object).Assembly.FullName!,
                        "CreateInstance" => "System.Text.StringBuilder",
                        "typed resource" => "payload",
                        _ => "IndirectReflection.payload",
                    };
                    instructions.LoadString(metadata.GetOrAddUserString(value));
                }
                else
                {
                    instructions.LoadConstantI4(0);
                    instructions.OpCode(ILOpCode.Box);
                    instructions.Token(TypeReference(typeof(bool)));
                }

                instructions.OpCode(ILOpCode.Stelem_ref);
            }
        }

        if (property)
        {
            LoadType(TypeReference(receiver));
            LoadName();
            Call(typeof(Type).GetMethod(nameof(Type.GetProperty), [typeof(string)])!);
            LoadReceiver();
            var count = dispatch switch { "property index" => 2, "property options" => 5, _ => 1 };
            if (count == 5)
            {
                instructions.LoadConstantI4(0);
                instructions.OpCode(ILOpCode.Ldnull);
            }

            if (count > 1) instructions.OpCode(ILOpCode.Ldnull);
            if (count == 5) instructions.OpCode(ILOpCode.Ldnull);
            Call(typeof(PropertyInfo).GetMethods().Single(method => method.Name == nameof(PropertyInfo.GetValue)
                && method.GetParameters().Length == count));
        }
        else if (dispatch.StartsWith("invoke member", StringComparison.Ordinal) || dispatch == "ireflect")
        {
            LoadType(TypeReference(receiver));
            LoadName();
            instructions.LoadConstantI4((int)(BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod));
            instructions.OpCode(ILOpCode.Ldnull);
            LoadReceiver();
            LoadArguments();
            var count = dispatch switch { "invoke member" => 5, "invoke member culture" => 6, _ => 8 };
            for (var index = 5; index < count; index++) instructions.OpCode(ILOpCode.Ldnull);
            Call((dispatch == "ireflect" ? typeof(IReflect) : typeof(Type)).GetMethods().Single(method =>
                method.Name == nameof(Type.InvokeMember) && method.GetParameters().Length == count));
        }
        else if (dispatch.Contains("delegate", StringComparison.Ordinal))
        {
            var open = dispatch.StartsWith("open", StringComparison.Ordinal);
            var delegateType = open ? typeof(Func<,>).MakeGenericType(receiver, inspection.ReturnType)
                : typeof(Func<>).MakeGenericType(inspection.ReturnType);
            if (dispatch.Contains("method delegate", StringComparison.Ordinal))
            {
                LoadMethod();
                if (dispatch.Contains("generic", StringComparison.Ordinal))
                {
                    if (!open) LoadReceiver();
                    var create = typeof(MethodInfo).GetMethods().Single(method => method.Name == nameof(MethodInfo.CreateDelegate)
                        && method.IsGenericMethodDefinition && method.GetParameters().Length == (open ? 0 : 1));
                    Call(create.MakeGenericMethod(delegateType));
                }
                else
                {
                    LoadType(SignatureType(delegateType));
                    if (!open) LoadReceiver();
                    Call(typeof(MethodInfo).GetMethod(nameof(MethodInfo.CreateDelegate),
                        open ? [typeof(Type)] : [typeof(Type), typeof(object)])!);
                }
            }
            else
            {
                LoadType(SignatureType(delegateType));
                if (!open) LoadReceiver();
                var named = dispatch.StartsWith("named", StringComparison.Ordinal);
                if (named) LoadName();
                else LoadMethod();
                var factoryParameters = new List<Type> { typeof(Type) };
                if (!open) factoryParameters.Add(typeof(object));
                factoryParameters.Add(named ? typeof(string) : typeof(MethodInfo));
                var options = dispatch == "named delegate options" ? 2
                    : dispatch.EndsWith("options", StringComparison.Ordinal) || dispatch.EndsWith("ignore case", StringComparison.Ordinal)
                        ? 1 : 0;
                for (var index = 0; index < options; index++)
                {
                    instructions.LoadConstantI4(1);
                    factoryParameters.Add(typeof(bool));
                }

                Call(typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), factoryParameters.ToArray())!);
            }

            if (open)
            {
                instructions.LoadConstantI4(1);
                instructions.OpCode(ILOpCode.Newarr);
                instructions.Token(TypeReference(typeof(object)));
                instructions.OpCode(ILOpCode.Dup);
                instructions.LoadConstantI4(0);
                LoadReceiver();
                instructions.OpCode(ILOpCode.Stelem_ref);
            }
            else instructions.OpCode(ILOpCode.Ldnull);
            Call(typeof(Delegate).GetMethod(nameof(Delegate.DynamicInvoke))!);
        }
        else if (dispatch == "function pointer")
        {
            LoadReceiver();
            LoadMethod();
            Call(typeof(MethodBase).GetProperty(nameof(MethodBase.MethodHandle))!.GetMethod!);
            instructions.StoreLocal(0);
            instructions.LoadLocalAddress(0);
            instructions.Call(MethodReference(typeof(RuntimeMethodHandle).GetMethod(nameof(RuntimeMethodHandle.GetFunctionPointer))!));
            var indirectSignature = new BlobBuilder();
            new BlobEncoder(indirectSignature).MethodSignature(isInstanceMethod: true).Parameters(0,
                value => EncodeType(value.Type(), inspection.ReturnType), _ => { });
            instructions.OpCode(ILOpCode.Calli);
            instructions.Token(metadata.AddStandaloneSignature(metadata.GetOrAddBlob(indirectSignature)));
        }
        else if (dispatch == "invoker")
        {
            LoadMethod();
            Call(typeof(MethodInvoker).GetMethod(nameof(MethodInvoker.Create))!);
            LoadReceiver();
            Call(typeof(MethodInvoker).GetMethod(nameof(MethodInvoker.Invoke), [typeof(object)])!);
        }
        else
        {
            if (dispatch == "array alias")
            {
                LoadType(owner);
                Call(typeof(Type).GetMethod(nameof(Type.GetMethods), Type.EmptyTypes)!);
                instructions.StoreLocal(0);
                instructions.LoadLocal(0);
                instructions.LoadConstantI4(0);
                LoadMethod();
                instructions.OpCode(ILOpCode.Stelem_ref);
                instructions.LoadLocal(0);
                instructions.LoadConstantI4(0);
                instructions.OpCode(ILOpCode.Ldelem_ref);
            }
            else LoadMethod(dispatch == "address" ? "ToString" : null);
            if (dispatch == "address")
            {
                instructions.StoreLocal(0);
                instructions.LoadLocalAddress(0);
                LoadMethod();
                instructions.OpCode(ILOpCode.Stind_ref);
                instructions.LoadLocal(0);
            }

            if (dispatch == "local")
            {
                instructions.StoreLocal(0);
                instructions.LoadLocal(0);
            }

            LoadReceiver();
            if (dispatch == "invoke options")
            {
                instructions.LoadConstantI4(0);
                instructions.OpCode(ILOpCode.Ldnull);
            }

            LoadArguments();
            if (dispatch == "invoke options") instructions.OpCode(ILOpCode.Ldnull);
            Call(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), dispatch == "invoke options"
                ? [typeof(object), typeof(BindingFlags), typeof(Binder), typeof(object[]), typeof(CultureInfo)]
                : [typeof(object), typeof(object[])])!);
        }

        var result = inspection.ReturnType;
        instructions.OpCode(result.IsValueType ? ILOpCode.Unbox_any : ILOpCode.Castclass);
        instructions.Token(SignatureType(result));
        if (result.IsArray)
        {
            instructions.OpCode(ILOpCode.Ldlen);
            instructions.OpCode(ILOpCode.Conv_i4);
        }
        else if (result == typeof(Stream))
        {
            Call(typeof(Stream).GetMethod(nameof(Stream.ReadByte))!);
            instructions.LoadConstantI4(42);
            instructions.OpCode(ILOpCode.Ceq);
        }
        else if (result == typeof(ManifestResourceInfo) || result == typeof(Type) || api == "CreateInstance")
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

        instructions.LoadConstantI4(42);
        instructions.OpCode(ILOpCode.Mul);
        instructions.OpCode(ILOpCode.Ret);
        var bodies = new BlobBuilder();
        var locals = new BlobBuilder();
        var localType = dispatch switch
        {
            "array alias" => typeof(MethodInfo[]), "function pointer" => typeof(RuntimeMethodHandle), _ => typeof(MethodInfo),
        };
        EncodeType(new BlobEncoder(locals).LocalVariableSignature(1).AddVariable().Type(), localType);
        var bodyEncoder = new MethodBodyStreamEncoder(bodies);
        var offset = bodyEncoder.AddMethodBody(instructions, maxStack: 12,
            localVariablesSignature: metadata.AddStandaloneSignature(metadata.GetOrAddBlob(locals)));
        byte[] readSignature = dispatch == "unknown" ? [0, 1, 8, 14] : [0, 0, 8];
        metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Static, MethodImplAttributes.IL,
            metadata.GetOrAddString("Read"), metadata.GetOrAddBlob(readSignature), offset, MetadataTokens.ParameterHandle(1));
        if (dispatch is "helper" or "starg")
        {
            var helper = new InstructionEncoder(new BlobBuilder());
            if (dispatch == "starg")
            {
                helper.LoadString(metadata.GetOrAddUserString("GetTypes"));
                helper.StoreArgument(1);
            }

            helper.LoadArgument(0);
            helper.LoadArgument(1);
            helper.OpCode(ILOpCode.Callvirt);
            helper.Token(MethodReference(typeof(Type).GetMethod(nameof(Type.GetMethod), [typeof(string)])!));
            helper.OpCode(ILOpCode.Ret);
            var helperOffset = bodyEncoder.AddMethodBody(helper);
            var helperSignature = new BlobBuilder();
            new BlobEncoder(helperSignature).MethodSignature().Parameters(2,
                value => EncodeType(value.Type(), typeof(MethodInfo)), parameters =>
                {
                    EncodeType(parameters.AddParameter().Type(), typeof(Type));
                    parameters.AddParameter().Type().String();
                });
            metadata.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString("Resolve"), metadata.GetOrAddBlob(helperSignature), helperOffset,
                MetadataTokens.ParameterHandle(1));
        }

        if (dispatch == "custom resolver")
        {
            var callback = new InstructionEncoder(new BlobBuilder());
            callback.OpCode(ILOpCode.Ldtoken);
            callback.Token(TypeReference(receiver));
            callback.Call(MethodReference(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!));
            callback.OpCode(ILOpCode.Ret);
            var callbackOffset = bodyEncoder.AddMethodBody(callback);
            var callbackSignature = new BlobBuilder();
            new BlobEncoder(callbackSignature).MethodSignature().Parameters(3,
                value => EncodeType(value.Type(), typeof(Type)), parameters =>
                {
                    EncodeType(parameters.AddParameter().Type(), typeof(Assembly));
                    parameters.AddParameter().Type().String();
                    parameters.AddParameter().Type().Boolean();
                });
            metadata.AddMethodDefinition(MethodAttributes.Private | MethodAttributes.Static, MethodImplAttributes.IL,
                metadata.GetOrAddString("ResolveType"), metadata.GetOrAddBlob(callbackSignature), callbackOffset,
                MetadataTokens.ParameterHandle(1));
        }

        if (api == "CreateInstance")
        {
            var initialize = new InstructionEncoder(new BlobBuilder());
            initialize.LoadArgument(0);
            initialize.Call(MethodReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            initialize.OpCode(ILOpCode.Ret);
            var initializeOffset = bodyEncoder.AddMethodBody(initialize);
            byte[] initializeSignature = [0x20, 0, 1];
            metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                MethodImplAttributes.IL, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(initializeSignature),
                initializeOffset, MetadataTokens.ParameterHandle(1));
        }

        var constructor = MethodReference(typeof(CLSCompliantAttribute).GetConstructor([typeof(bool)])!);
        byte[] attribute = [1, 0, 1, 0, 0];
        foreach (var entity in new EntityHandle[] { assembly, module })
            metadata.AddCustomAttribute(entity, constructor, metadata.GetOrAddBlob(attribute));
        var resources = new BlobBuilder();
        resources.WriteInt32(3);
        byte[] payload = [42, 17, 255];
        resources.WriteBytes(payload);
        metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString("IndirectReflection.payload"), default, 0);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), bodies, managedResources: resources, flags: CorFlags.ILOnly).Serialize(image);
        return image.ToArray();
    }
}
