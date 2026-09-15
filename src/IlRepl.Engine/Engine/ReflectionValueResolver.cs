using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Remoting;

namespace IlRepl.Engine;

/// <summary>
/// Resolves finite reflection inputs from accepted IL without evaluating user code.
/// </summary>
internal sealed partial class ReflectionValueResolver(
    MethodBase selected, IReadOnlyDictionary<MethodBase, MethodEditBody?> bodies, Func<ResolvedMethod, MethodBase> resolveMethod)
{
    private const int Limit = 256;
    private readonly HashSet<(MethodBase Method, int Origin)> _active = [];
    private readonly Dictionary<(MethodBase Method, int Argument), object?[]?> _parameters = [];

    /// <summary>
    /// Resolves a call argument, using minus one for an instance receiver and null for an unknown value set.
    /// </summary>
    internal object?[]? Argument(MethodEditBody body, int position, int parameter)
    {
        if (body.State.Entries[position].Instruction?.Operand is not ResolvedMethod resolved) return null;
        var method = resolveMethod(resolved);
        var values = body.State.Analysis.Before[position]?.Values;
        var slot = values?.Length - method.GetParameters().Length + parameter;
        return values is not null && slot is >= 0 && slot < values.Length ? Value(body, values[slot.Value]) : null;
    }

    /// <summary>
    /// Resolves a source spelling with runtime parsing and loaded assembly metadata, without calling user resolvers.
    /// </summary>
    internal static Type? NamedType(string name, Assembly context, bool ignoreCase = false)
    {
        try
        {
            return Type.GetType(name, identity => LoadedAssembly(identity, context),
                (assembly, text, ignore) => assembly?.GetType(text, false, ignore)
                    ?? (assembly is null ? context.GetType(text, false, ignore)
                        ?? typeof(object).Assembly.GetType(text, false, ignore) : null),
                false, ignoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or TypeLoadException or AmbiguousMatchException
            or FileNotFoundException or FileLoadException or BadImageFormatException or NotSupportedException)
        {
            return null;
        }
    }

    private static Assembly? LoadedAssembly(AssemblyName identity, Assembly context)
    {
        return new[] { context }.Concat(AppDomain.CurrentDomain.GetAssemblies()).FirstOrDefault(assembly =>
            AssemblyName.ReferenceMatchesDefinition(identity, assembly.GetName())
                && (identity.Version is null || identity.Version <= assembly.GetName().Version));
    }

    /// <summary>
    /// Resolves an activation assembly from metadata already loaded into this process.
    /// </summary>
    internal static Assembly? ActivationAssembly(string? name, Assembly context, bool fromFile)
    {
        if (name is null) return fromFile ? null : context;
        try
        {
            if (!fromFile) return LoadedAssembly(new AssemblyName(name), context);
            var path = Path.GetFullPath(name);
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => !assembly.IsDynamic
                && !string.IsNullOrEmpty(assembly.Location) && Path.GetFullPath(assembly.Location) == path);
            if (loaded is not null) return loaded;
            using var stream = File.OpenRead(path);
            using var image = new PEReader(stream);
            var metadata = image.GetMetadataReader();
            var module = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            return new[] { context }.Concat(AppDomain.CurrentDomain.GetAssemblies())
                .FirstOrDefault(assembly => assembly.ManifestModule.ModuleVersionId == module);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves every possible case-sensitivity setting for a type lookup or activation overload.
    /// </summary>
    internal bool[] LookupCasing(MethodEditBody body, int position, MethodBase method)
    {
        var parameters = method.GetParameters();
        var index = Array.FindIndex(parameters, parameter => parameter.Name == "ignoreCase");
        if (index < 0) return [false];
        var values = Argument(body, position, index);
        return values is null || values.Any(value => value is not int) ? [false, true]
            : values.Cast<int>().Select(value => value != 0).Distinct().ToArray();
    }

    private object?[]? Value(MethodEditBody body, FlowValue<Type> value)
    {
        if (value.Origins.Length == 0) return null;
        return Merge(value.Origins.Select(origin => Origin(body, origin)));
    }

    private object?[]? Origin(MethodEditBody body, int position)
    {
        var instruction = body.State.Entries[position].Instruction;
        if (instruction?.ArgumentIndex is { } argument && instruction.Op.Name?.StartsWith("ldarg", StringComparison.Ordinal) == true
            && _parameters.TryGetValue((body.Method, argument), out var known)) return known;
        if (_active.Count >= Limit || !_active.Add((body.Method, position))) return null;
        try
        {
            if (instruction is null) return null;
            var op = instruction.Op;
            if (op == OpCodes.Ldstr) return [instruction.Operand];
            if (op == OpCodes.Ldnull) return [null];
            if (op == OpCodes.Ldtoken || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
                return [instruction.Operand is ResolvedMethod method ? resolveMethod(method) : instruction.Operand];
            if (op.Name?.StartsWith("ldc.i4", StringComparison.Ordinal) == true)
                return [op == OpCodes.Ldc_I4_M1 ? -1 : op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S
                    ? Convert.ToInt32(instruction.Operand) : op.Value - OpCodes.Ldc_I4_0.Value];
            if (op == OpCodes.Castclass || op == OpCodes.Isinst || op == OpCodes.Ldind_Ref || op == OpCodes.Ldobj)
                return Stack(body, position, 1);
            if (op.Name?.StartsWith("ldelem", StringComparison.Ordinal) == true)
                return Collection(body, position, 2) ?? ArrayElementValues(body, position);
            if (instruction.LocalIndex is { } local && op.Name?.StartsWith("ldloc", StringComparison.Ordinal) == true)
                return Local(body, local);
            if (instruction.ArgumentIndex is { } parameter && op.Name?.StartsWith("ldarg", StringComparison.Ordinal) == true)
                return Parameter(body, parameter);
            if (instruction.Operand is not ResolvedMethod resolved) return null;
            var target = resolveMethod(resolved);
            if (op == OpCodes.Newobj)
                return typeof(Delegate).IsAssignableFrom(target.DeclaringType) ? Stack(body, position, 1)
                    : [new ReflectedInstance(target.DeclaringType!)];
            if (op != OpCodes.Call && op != OpCodes.Callvirt) return null;
            return Call(body, position, target);
        }
        finally
        {
            _active.Remove((body.Method, position));
        }
    }

    /// <summary>
    /// Resolves a stack slot counted from the top, returning null when its complete value set is unknown.
    /// </summary>
    internal object?[]? Stack(MethodEditBody body, int position, int fromTop)
    {
        var values = body.State.Analysis.Before[position]?.Values;
        return values is not null && values.Length >= fromTop ? Value(body, values[^fromTop]) : null;
    }

    private object?[]? Collection(MethodEditBody body, int position, int fromTop)
    {
        var stack = body.State.Analysis.Before[position]?.Values;
        if (stack is null || stack.Length < fromTop || stack[^fromTop].Origins is not { Length: 1 } origins) return null;
        var origin = origins[0];
        if (body.State.Entries[origin].Instruction?.Operand is not ResolvedMethod resolved) return null;
        var method = resolveMethod(resolved);
        if (method.DeclaringType != typeof(Type) && method.DeclaringType != typeof(TypeInfo)
            && method.DeclaringType != typeof(RuntimeReflectionExtensions)
            && !(typeof(MethodBase).IsAssignableFrom(method.DeclaringType!)
                && method.Name is nameof(MethodBase.GetParameters) or nameof(MethodBase.GetGenericArguments))) return null;
        for (var index = origin + 1; index < position; index++)
        {
            var instruction = body.State.Entries[index].Instruction;
            if (instruction is not null && instruction.Op != OpCodes.Nop
                && instruction.Op.Name?.StartsWith("ldc.i4", StringComparison.Ordinal) != true) return null;
        }

        return Value(body, stack[^fromTop]);
    }

    private object?[]? Local(MethodEditBody body, int local)
    {
        var stores = body.State.Entries.Select((entry, index) => (entry.Instruction, index))
            .Where(entry => entry.Instruction?.LocalIndex == local).ToArray();
        if (stores.Any(entry => entry.Instruction!.Op.Name?.StartsWith("ldloca", StringComparison.Ordinal) == true
            && !ReadsMethodHandle(body, entry.index))) return null;
        var values = stores.Where(entry => entry.Instruction!.Op.Name?.StartsWith("stloc", StringComparison.Ordinal) == true)
            .Select(entry => Stack(body, entry.index, 1)).ToArray();
        return values.Length == 0 ? null : Merge(values);
    }

    private bool ReadsMethodHandle(MethodEditBody body, int position)
    {
        var next = body.State.Entries.Skip(position + 1).Select(entry => entry.Instruction)
            .FirstOrDefault(instruction => instruction is not null && instruction.Op != OpCodes.Nop);
        return next?.Op == OpCodes.Call && next.Operand is ResolvedMethod resolved
            && resolveMethod(resolved) is { DeclaringType: { } owner, Name: nameof(RuntimeMethodHandle.GetFunctionPointer) }
            && owner == typeof(RuntimeMethodHandle);
    }

    private object?[]? Parameter(MethodEditBody body, int argument)
    {
        if (body.Method == selected) return null;
        var key = (body.Method, argument);
        if (_parameters.TryGetValue(key, out var known)) return known;
        var instructions = body.State.Entries.Select((entry, index) => (entry.Instruction, index)).ToArray();
        if (instructions.Any(entry => entry.Instruction?.ArgumentIndex == argument
            && entry.Instruction.Op.Name?.StartsWith("ldarga", StringComparison.Ordinal) == true)) return null;
        var parameter = argument - (body.Method.IsStatic ? 0 : 1);
        var values = instructions.Where(entry => entry.Instruction?.ArgumentIndex == argument
                && entry.Instruction.Op.Name?.StartsWith("starg", StringComparison.Ordinal) == true)
            .Select(entry => Stack(body, entry.index, 1)).ToList();
        foreach (var caller in bodies.Values.OfType<MethodEditBody>())
        {
            for (var position = 0; position < caller.State.Entries.Count; position++)
            {
                if (caller.State.Entries[position].Instruction is not { Operand: ResolvedMethod resolved } instruction) continue;
                var target = resolveMethod(resolved);
                if (IlAsmRenderer.DefinitionOf(target) == body.Method)
                {
                    if (instruction.Op != OpCodes.Call && instruction.Op != OpCodes.Callvirt && instruction.Op != OpCodes.Newobj)
                        return null;
                    values.Add(Argument(caller, position, parameter));
                }
            }
        }

        var inputs = values.Count == 0 ? null : Merge(values);
        _parameters[key] = inputs;
        if (inputs is null) return null;
        foreach (var caller in bodies.Values.OfType<MethodEditBody>())
        {
            for (var position = 0; position < caller.State.Entries.Count; position++)
            {
                if (caller.State.Entries[position].Instruction is not { Operand: ResolvedMethod resolved } instruction
                    || instruction.Op != OpCodes.Call && instruction.Op != OpCodes.Callvirt) continue;
                var target = resolveMethod(resolved);
                if (IlAsmRenderer.DefinitionOf(target) == body.Method) continue;
                if (target is MethodInfo info && (typeof(MethodBase).IsAssignableFrom(info.ReturnType)
                    || info.ReturnType.IsArray && typeof(MemberInfo).IsAssignableFrom(info.ReturnType.GetElementType())
                    || typeof(Delegate).IsAssignableFrom(info.ReturnType)))
                {
                    var targets = Call(caller, position, target);
                    if (targets is null || targets.OfType<MethodBase>().Any(member => IlAsmRenderer.DefinitionOf(member) == body.Method))
                    {
                        _parameters[key] = null;
                        return null;
                    }
                }
            }
        }

        return inputs;
    }

    private object?[]? Call(MethodEditBody body, int position, MethodBase method)
    {
        if (method.DeclaringType == typeof(Array) && method.Name == nameof(Array.GetValue))
            return ArrayElementValues(body, position, method.GetParameters().Length + 1);
        var owner = method.DeclaringType;
        var name = method.Name;
        object?[]? Input(int parameter = -1) => Argument(body, position, parameter);
        if (owner == typeof(Type) && name == nameof(Type.GetTypeFromHandle)
            || owner == typeof(MethodBase) && name == nameof(MethodBase.GetMethodFromHandle)
            || owner == typeof(FieldInfo) && name == nameof(FieldInfo.GetFieldFromHandle)) return Input(0);
        if (owner is not null && typeof(MethodBase).IsAssignableFrom(owner) && name == nameof(MethodBase.GetParameters))
        {
            var methods = Input();
            return methods is null ? null : Merge(methods.Select(value => value is MethodBase member
                ? member.GetParameters().Cast<object?>().ToArray() : null));
        }
        if (owner is not null && (typeof(MethodBase).IsAssignableFrom(owner) || typeof(Type).IsAssignableFrom(owner))
            && name == nameof(Type.GetGenericArguments))
        {
            var receivers = Input();
            return receivers is null ? null : Merge(receivers.Select(value => value is Type type
                ? type.GetGenericArguments().Cast<object?>().ToArray()
                : value is MethodBase member ? member.GetGenericArguments().Cast<object?>().ToArray() : null));
        }
        if (owner == typeof(IntrospectionExtensions) && name == nameof(IntrospectionExtensions.GetTypeInfo)) return Input(0);
        if (owner == typeof(TypeInfo) && name == nameof(TypeInfo.AsType)) return Input();
        if (name == "get_MethodHandle" && owner is not null && typeof(MethodBase).IsAssignableFrom(owner)) return Input();
        if (owner == typeof(Assembly) && name == nameof(Assembly.GetExecutingAssembly)) return [body.Method.Module.Assembly];
        if (owner == typeof(object) && name == nameof(GetType))
            return Map(Input(), value => value is ReflectedInstance instance ? instance.Type : value?.GetType());
        if (name == "get_Assembly" && owner == typeof(Type)) return Map(Input(), value => (value as Type)?.Assembly);
        if (name == "get_Assembly" && owner is not null && typeof(Module).IsAssignableFrom(owner))
            return Map(Input(), value => (value as Module)?.Assembly);
        if (name == "get_Module" && owner is not null && typeof(MemberInfo).IsAssignableFrom(owner))
            return Map(Input(), value => (value as MemberInfo)?.Module);
        if (name == "get_ManifestModule" && owner == typeof(Assembly))
            return Map(Input(), value => (value as Assembly)?.ManifestModule);
        if (name == "get_DeclaringType" && owner is not null && typeof(MemberInfo).IsAssignableFrom(owner))
            return Map(Input(), value => (value as MemberInfo)?.DeclaringType);
        if (name is "get_Name" or "get_FullName" or "get_AssemblyQualifiedName")
        {
            if (owner == typeof(Type) || owner == typeof(MemberInfo))
                return Map(Input(), value => value is Type type ? name == "get_Name" ? type.Name
                    : name == "get_FullName" ? type.FullName : type.AssemblyQualifiedName : (value as MemberInfo)?.Name);
            if (owner == typeof(Assembly)) return Map(Input(), value => (value as Assembly)?.FullName);
        }

        if (owner == typeof(Type) && method.IsStatic && name == nameof(Type.GetType))
        {
            if (method.GetParameters().Length >= 3 && method.GetParameters()[1].ParameterType != typeof(bool)
                && (Input(1) is not { } assemblies || assemblies.Any(value => value is not null)
                    || Input(2) is not { } types || types.Any(value => value is not null))) return null;
            return Merge(LookupCasing(body, position, method).Select(ignore => Map(Input(0), value => value is string text
                ? NamedType(text, body.Method.Module.Assembly, ignore) : null)));
        }
        if (owner is not null && (typeof(Assembly).IsAssignableFrom(owner) || typeof(Module).IsAssignableFrom(owner))
            && name == nameof(Type.GetType) && !method.IsStatic)
            return Merge(LookupCasing(body, position, method).Select(ignore => Product(Input(), Input(0), (receiver, value) =>
                value is not string text ? null : receiver is Assembly assembly ? NamedType(text, assembly, ignore)
                    : receiver is Module module ? NamedType(text, module.Assembly, ignore) : null)));
        if (owner == typeof(Assembly) && name == nameof(Assembly.CreateInstance))
            return Merge(LookupCasing(body, position, method).Select(ignore => Product(Input(), Input(0), (receiver, value) =>
                receiver is Assembly assembly && value is string text && NamedType(text, assembly, ignore) is { } type
                    ? new ReflectedInstance(type) : null)));
        if (owner == typeof(Activator) && name is nameof(Activator.CreateInstance) or nameof(Activator.CreateInstanceFrom))
        {
            if (method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Type))
                return Map(Input(0), value => value is Type type ? new ReflectedInstance(type) : null);
            if (method.GetParameters() is { Length: >= 2 } parameters && parameters[0].ParameterType == typeof(string))
                return Merge(LookupCasing(body, position, method).Select(ignore => Product(Input(0), Input(1), (assembly, value) =>
                    assembly is null or string && value is string text
                        && ActivationAssembly((string?)assembly, body.Method.Module.Assembly,
                            name == nameof(Activator.CreateInstanceFrom)) is { } context
                        && NamedType(text, context, ignore) is { } type ? new ReflectedInstance(typeof(ObjectHandle), type) : null)));
        }
        if (owner == typeof(ObjectHandle) && name == nameof(ObjectHandle.Unwrap))
            return Map(Input(), value => value is ReflectedInstance { WrappedType: { } type } ? new ReflectedInstance(type) : null);
        if (owner == typeof(string) && name == nameof(string.Concat)
            && method.GetParameters().All(parameter => parameter.ParameterType == typeof(string)))
        {
            object?[]? result = [""];
            for (var index = 0; index < method.GetParameters().Length; index++)
                result = Product(result, Input(index), (left, right) => (left is null or string) && (right is null or string)
                    ? (string?)left + (string?)right : null);
            return result;
        }

        if (owner is not null && (typeof(Type).IsAssignableFrom(owner) || owner == typeof(IReflect)
                || owner == typeof(RuntimeReflectionExtensions)))
        {
            if (HasCustomBinder(body, position, method)) return null;
            var receiver = method.IsStatic ? Input(0) : Input();
            var argument = method.IsStatic ? 1 : 0;
            if (name is nameof(Type.GetConstructor) or nameof(Type.GetConstructors) or "get_DeclaredConstructors")
                return Members(receiver, [null], MemberTypes.Constructor);
            if (name == "get_TypeInitializer") return Members(receiver, [ConstructorInfo.TypeConstructorName], MemberTypes.Constructor);
            if (name == nameof(Type.GetEvent)) return Members(receiver, Input(argument), MemberTypes.Event);
            if (name is "GetMethod" or "GetRuntimeMethod" or "GetProperty" or "GetRuntimeProperty" or "GetField" or "GetRuntimeField")
                return Members(receiver, Input(argument), name.Contains("Property", StringComparison.Ordinal) ? MemberTypes.Property
                    : name.Contains("Field", StringComparison.Ordinal) ? MemberTypes.Field : MemberTypes.Method);
            if (name is "GetMethods" or "GetRuntimeMethods" or "GetProperties" or "GetRuntimeProperties" or "GetMembers")
                return Members(receiver, [null], name.Contains("Propert", StringComparison.Ordinal) ? MemberTypes.Property
                    : name == "GetMembers" ? MemberTypes.All : MemberTypes.Method);
            if (name == nameof(Type.GetNestedType))
                return Members(receiver, Input(argument), MemberTypes.NestedType);
        }

        if (owner is not null && typeof(PropertyInfo).IsAssignableFrom(owner) && name == nameof(PropertyInfo.GetGetMethod))
            return Map(Input(), value => (value as PropertyInfo)?.GetMethod);
        if (owner is not null && typeof(MethodInfo).IsAssignableFrom(owner)
            && name is nameof(MethodInfo.MakeGenericMethod) or nameof(MethodInfo.GetGenericMethodDefinition)
                or nameof(MethodInfo.CreateDelegate))
            return Input();
        if ((owner == typeof(MethodInvoker) || owner == typeof(ConstructorInvoker)) && name == nameof(MethodInvoker.Create))
            return Input(0);
        if (name == nameof(ConstructorInfo.Invoke) && (owner == typeof(ConstructorInvoker)
            || owner is not null && typeof(ConstructorInfo).IsAssignableFrom(owner) && method.GetParameters().Length is 1 or 4))
            return Map(Input(), value => value is ConstructorInfo { IsStatic: false, DeclaringType: { } type }
                ? new ReflectedInstance(type) : null);
        if (owner == typeof(Delegate) && name == nameof(Delegate.CreateDelegate)) return DelegateTargets(body, position, method);
        if (owner == typeof(Delegate) && name == "get_Method") return Input();
        if (owner == typeof(Enumerable) && name is nameof(Enumerable.First) or nameof(Enumerable.FirstOrDefault)
            or nameof(Enumerable.Single) or nameof(Enumerable.SingleOrDefault) or nameof(Enumerable.ElementAt))
            return Collection(body, position, method.GetParameters().Length);
        var definition = IlAsmRenderer.DefinitionOf(method);
        if (!bodies.TryGetValue(definition, out var called) || called is null) return null;
        return Merge(called.State.Entries.Select((entry, index) => (entry, index))
            .Where(pair => pair.entry.Instruction?.Op == OpCodes.Ret).Select(pair => Stack(called, pair.index, 1)));
    }

    /// <summary>
    /// Resolves the members selected by a name-based invocation or delegate binding.
    /// </summary>
    internal object?[]? NamedMembers(MethodEditBody body, int position, int receiver, int name)
        => body.State.Entries[position].Instruction?.Operand is ResolvedMethod resolved
            && !HasCustomBinder(body, position, resolveMethod(resolved))
                ? Members(Argument(body, position, receiver), Argument(body, position, name), MemberTypes.All) : null;

    private bool HasCustomBinder(MethodEditBody body, int position, MethodBase method)
    {
        var parameters = method.GetParameters();
        for (var index = 0; index < parameters.Length; index++)
            if (parameters[index].ParameterType == typeof(Binder)
                && (Argument(body, position, index) is not { } values || values.Any(value => value is not null))) return true;
        return false;
    }

    /// <summary>
    /// Resolves every possible target of a supported delegate factory overload.
    /// </summary>
    internal object?[]? DelegateTargets(MethodEditBody body, int position, MethodBase method)
    {
        var parameters = method.GetParameters();
        var member = Array.FindIndex(parameters, parameter => parameter.ParameterType == typeof(MethodInfo));
        if (member >= 0) return Argument(body, position, member);
        var names = Argument(body, position, 2);
        return Members(Argument(body, position, 1), names, MemberTypes.Method, boundInstance: parameters[1].ParameterType != typeof(Type))
            ?? Map(names, value => value is string name ? new ReflectedDelegateName(name) : null);
    }

    private static object?[]? Members(object?[]? receivers, object?[]? names, MemberTypes kinds, bool boundInstance = false)
    {
        if (receivers is null || names is null) return null;
        var members = new List<object?>();
        foreach (var receiver in receivers)
        {
            var type = boundInstance ? receiver is ReflectedInstance instance ? instance.Type : receiver?.GetType() : receiver as Type;
            if (type is null) return null;
            foreach (var name in names)
                members.AddRange(type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .Where(member => (member.MemberType & kinds) != 0
                        && (name is null || name is string text && member.Name.Equals(text, StringComparison.OrdinalIgnoreCase))));
        }

        return members.Count > Limit ? null : members.Distinct().ToArray();
    }

    private static object?[]? Map(object?[]? values, Func<object?, object?> map)
    {
        if (values is null) return null;
        var mapped = values.Select(map).ToArray();
        return mapped.Any(value => value is null) ? null : mapped.Distinct().ToArray();
    }

    private static object?[]? Product(object?[]? left, object?[]? right, Func<object?, object?, object?> map)
    {
        if (left is null || right is null || left.Length * right.Length > Limit) return null;
        var mapped = left.SelectMany(first => right.Select(second => map(first, second))).ToArray();
        return mapped.Any(value => value is null) ? null : mapped.Distinct().ToArray();
    }

    private static object?[]? Merge(IEnumerable<object?[]?> inputs)
    {
        var values = new HashSet<object?>();
        foreach (var input in inputs)
        {
            if (input is null) return null;
            values.UnionWith(input);
            if (values.Count > Limit) return null;
        }

        return values.ToArray();
    }
}
