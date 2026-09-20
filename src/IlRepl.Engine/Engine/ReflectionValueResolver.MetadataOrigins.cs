using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Tracks copied metadata producers across transports without equating an original assembly with every reference to that assembly.
/// </summary>
internal sealed partial class ReflectionValueResolver
{
    private readonly HashSet<(MethodBase Method, int Origin)> _copiedMetadataVisited = [];
    private readonly HashSet<(MethodBase Method, int Origin, Func<object, bool> Predicate)> _ordinaryMetadataOrigins = [];
    private readonly Queue<(MethodEditBody Body, int Origin)> _copiedMetadataPending = [];
    private readonly Dictionary<(MethodBase Method, int Position, int FromTop, Func<object, bool> Predicate), bool>
        _copiedMetadataQueries = [];
    private bool _copiedMetadataRunning;
    private bool _copiedMetadataFound;

    /// <summary>
    /// Finds copied metadata in a stack value or its stored contents using the completed family's member ownership predicate.
    /// </summary>
    internal bool HasCopiedMetadata(MethodEditBody body, int position, int fromTop, Func<object, bool> isCopiedMember)
    {
        var key = (body.Method, position, fromTop, isCopiedMember);
        if (_copiedMetadataRunning)
        {
            if (_copiedMetadataQueries.TryGetValue(key, out var cached))
            {
                _copiedMetadataFound |= cached;
            }
            else
            {
                EnqueueCopiedMetadata(body, position, fromTop, isCopiedMember);
            }

            return false;
        }

        if (_copiedMetadataQueries.TryGetValue(key, out var known))
        {
            return known;
        }

        _copiedMetadataVisited.Clear();
        _copiedMetadataFound = false;
        _copiedMetadataRunning = true;
        try
        {
            EnqueueCopiedMetadata(body, position, fromTop, isCopiedMember);
            while (!_copiedMetadataFound && _copiedMetadataPending.TryDequeue(out var next))
            {
                if (CopiedMetadataOrigin(next.Body, next.Origin, isCopiedMember))
                {
                    _copiedMetadataFound = true;
                }
            }

            if (!_copiedMetadataFound)
            {
                foreach (var visited in _copiedMetadataVisited)
                {
                    _ordinaryMetadataOrigins.Add((visited.Method, visited.Origin, isCopiedMember));
                }
            }

            // Nested queries only schedule alternatives; only the outer traversal has a complete answer to cache.
            _copiedMetadataQueries.Add(key, _copiedMetadataFound);
            return _copiedMetadataFound;
        }
        finally
        {
            _copiedMetadataRunning = false;
            _copiedMetadataPending.Clear();
        }
    }

    private void EnqueueCopiedMetadata(MethodEditBody body, int position, int fromTop, Func<object, bool> isCopiedMember)
    {
        var values = body.State.Analysis.Before[position]?.Values;
        if (values is null || values.Length < fromTop)
        {
            return;
        }

        foreach (var origin in values[^fromTop].Origins
            .Where(origin => !_ordinaryMetadataOrigins.Contains((body.Method, origin, isCopiedMember)))
            .Where(origin => _copiedMetadataVisited.Add((body.Method, origin))))
        {
            _copiedMetadataPending.Enqueue((body, origin));
        }
    }

    private bool CopiedMetadataOrigin(MethodEditBody body, int position, Func<object, bool> isCopiedMember)
    {
        var instruction = body.State.Entries[position].Instruction;
        if (instruction is null)
        {
            return false;
        }

        var op = instruction.Op;
        if (op == OpCodes.Ldnull || op == OpCodes.Ldstr)
        {
            return false;
        }

        if (op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
        {
            return instruction.Operand is ResolvedMethod pointer && CopiedCallbackMetadata(resolveMethod(pointer), isCopiedMember);
        }

        if (op == OpCodes.Ldtoken)
        {
            return instruction.Operand is ResolvedMethod token ? isCopiedMember(resolveMethod(token))
                : instruction.Operand is { } operand && isCopiedMember(operand);
        }

        if (op == OpCodes.Castclass || op == OpCodes.Isinst || op == OpCodes.Ldind_Ref || op == OpCodes.Ldobj
            || op == OpCodes.Box || op == OpCodes.Unbox_Any || op == OpCodes.Unbox || op == OpCodes.Conv_I || op == OpCodes.Conv_U)
        {
            return HasCopiedMetadata(body, position, 1, isCopiedMember);
        }

        if (op == OpCodes.Newarr)
        {
            return CopiedArrayContents(body, position, null, isCopiedMember);
        }

        if (op.Name?.StartsWith("ldelem", StringComparison.Ordinal) == true || op == OpCodes.Ldelema)
        {
            return CopiedArrayContents(body, position, 2, isCopiedMember)
                || CopiedResolvedMember(body, position, isCopiedMember);
        }

        if (instruction.LocalIndex is { } local && op.Name?.StartsWith("ldloc", StringComparison.Ordinal) == true)
        {
            return body.State.Entries.Select((entry, index) => (entry.Instruction, index)).Any(entry =>
                entry.Instruction?.LocalIndex == local
                    && entry.Instruction.Op.Name?.StartsWith("stloc", StringComparison.Ordinal) == true
                    && HasCopiedMetadata(body, entry.index, 1, isCopiedMember))
                || CopiedAddressWrites((body.Method, local, false), isCopiedMember);
        }

        if (instruction.Operand is FieldInfo field && (op == OpCodes.Ldfld || op == OpCodes.Ldsfld
            || op == OpCodes.Ldflda || op == OpCodes.Ldsflda))
        {
            return CopiedFieldContents(field, isCopiedMember);
        }

        if (instruction.ArgumentIndex is { } argument && op.Name?.StartsWith("ldarg", StringComparison.Ordinal) == true)
        {
            return CopiedMetadataParameter(body, argument, isCopiedMember);
        }

        if (instruction.Operand is not ResolvedMethod resolved)
        {
            return false;
        }

        var method = resolveMethod(resolved);
        if (op == OpCodes.Newobj)
        {
            if (typeof(Delegate).IsAssignableFrom(method.DeclaringType))
            {
                return HasCopiedMetadata(body, position, 2, isCopiedMember)
                    || HasCopiedMetadata(body, position, 1, isCopiedMember);
            }

            return Enumerable.Range(1, method.GetParameters().Length)
                    .Any(index => HasCopiedMetadata(body, position, index, isCopiedMember))
                || method.DeclaringType!.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(memberField => CopiedFieldContents(memberField, isCopiedMember));
        }

        return (op == OpCodes.Call || op == OpCodes.Callvirt) && CopiedMetadataCall(body, position, method, isCopiedMember);
    }

    private bool CopiedResolvedMember(MethodEditBody body, int position, Func<object, bool> isCopiedMember)
        => Origin(body, position)?.Any(value => value is MemberInfo or ParameterInfo && isCopiedMember(value)) == true;

    private bool CopiedFieldContents(FieldInfo field, Func<object, bool> isCopiedMember)
        => bodies.Values.OfType<MethodEditBody>().Any(candidate =>
            candidate.State.Entries.Select((entry, index) => (entry.Instruction, index)).Any(entry =>
                entry.Instruction?.Operand is FieldInfo stored && stored == field
                    && (entry.Instruction.Op == OpCodes.Stfld || entry.Instruction.Op == OpCodes.Stsfld)
                    && HasCopiedMetadata(candidate, entry.index, 1, isCopiedMember)))
            || CopiedAddressWrites(field, isCopiedMember);

    private bool CopiedMetadataCall(MethodEditBody body, int position, MethodBase method, Func<object, bool> isCopiedMember)
    {
        var owner = method.DeclaringType;
        var name = method.Name;
        var framework = owner?.Assembly == typeof(Type).Assembly;
        if (owner == typeof(Assembly) && name == nameof(Assembly.GetExecutingAssembly))
        {
            return true;
        }

        if (owner == typeof(MethodBase) && name == nameof(MethodBase.GetCurrentMethod))
        {
            return true;
        }

        if (owner == typeof(Assembly) && name == nameof(Assembly.GetAssembly))
        {
            return HasCopiedMetadata(body, position, 1, isCopiedMember);
        }

        var readsHandle = owner == typeof(Type) ? name == nameof(Type.GetTypeFromHandle)
            : owner == typeof(MethodBase) ? name == nameof(MethodBase.GetMethodFromHandle)
            : owner == typeof(FieldInfo) ? name == nameof(FieldInfo.GetFieldFromHandle)
            : owner == typeof(IntrospectionExtensions) && name == nameof(IntrospectionExtensions.GetTypeInfo);
        if (framework && readsHandle)
        {
            return HasCopiedMetadata(body, position, method.GetParameters().Length, isCopiedMember);
        }

        if (framework && !method.IsStatic && (name is "get_Assembly" or "get_Module" or "get_ManifestModule"
                or "get_TypeHandle" or "get_MethodHandle"
                or "get_FieldHandle" or "get_ModuleHandle" || owner == typeof(TypeInfo) && name == nameof(TypeInfo.AsType)))
        {
            return HasCopiedMetadata(body, position, method.GetParameters().Length + 1, isCopiedMember);
        }

        var handle = owner == typeof(RuntimeTypeHandle) || owner == typeof(RuntimeMethodHandle) || owner == typeof(RuntimeFieldHandle);
        if (handle && name is "get_Value" or "FromIntPtr" or "ToIntPtr")
        {
            return HasCopiedMetadata(body, position, 1, isCopiedMember);
        }

        if (owner == typeof(Array) && name == nameof(Array.GetValue))
        {
            return CopiedArrayContents(body, position, method.GetParameters().Length + 1, isCopiedMember);
        }

        if (owner == typeof(Enumerable) && name is nameof(Enumerable.First) or nameof(Enumerable.FirstOrDefault)
            or nameof(Enumerable.Single) or nameof(Enumerable.SingleOrDefault) or nameof(Enumerable.ElementAt)
            or nameof(Enumerable.ElementAtOrDefault) or nameof(Enumerable.ToArray))
        {
            return CopiedArrayContents(body, position, method.GetParameters().Length, isCopiedMember,
                    name is nameof(Enumerable.ElementAt) or nameof(Enumerable.ElementAtOrDefault))
                || method.GetParameters().Select((parameter, index) => (parameter, index)).Any(argument =>
                    argument.parameter.Name == "defaultValue"
                        && HasCopiedMetadata(body, position, method.GetParameters().Length - argument.index, isCopiedMember));
        }

        if (name == nameof(MethodInfo.CreateDelegate) && framework)
        {
            return (owner == typeof(Delegate) ? DelegateTargets(body, position, method) : Argument(body, position, -1))
                    ?.OfType<MethodBase>().Any(target => CopiedCallbackMetadata(target, isCopiedMember)) == true
                || Enumerable.Range(0, method.GetParameters().Length).Any(index =>
                    method.GetParameters()[index].ParameterType == typeof(object)
                        && HasCopiedMetadata(body, position, method.GetParameters().Length - index, isCopiedMember));
        }

        if (owner == typeof(RuntimeMethodHandle) && name == nameof(RuntimeMethodHandle.GetFunctionPointer))
        {
            return Argument(body, position, -1)?.OfType<MethodBase>()
                .Any(target => CopiedCallbackMetadata(target, isCopiedMember)) == true;
        }

        var known = name == "get_ReflectedType" && framework
            ? Map(Argument(body, position, -1), value => (value as MemberInfo)?.ReflectedType)
            : name == "get_Member" && framework ? Map(Argument(body, position, -1), value => (value as ParameterInfo)?.Member)
            : owner == typeof(Type) && name == nameof(Type.GetGenericTypeDefinition)
                ? Map(Argument(body, position, -1), value => value is Type { IsGenericType: true } type
                    ? type.GetGenericTypeDefinition() : null) : Origin(body, position);
        if (known?.OfType<ReflectedInstance>().Any(instance =>
            instance.Type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(field => CopiedFieldContents(field, isCopiedMember))) == true)
        {
            return true;
        }

        var result = (method as MethodInfo)?.ReturnType;
        if (result?.IsArray == true)
        {
            result = result.GetElementType();
        }

        var descriptor = result is not null && (typeof(MemberInfo).IsAssignableFrom(result)
            || typeof(ParameterInfo).IsAssignableFrom(result));
        if (descriptor && known is not null && known.All(value => value is null or MemberInfo or ParameterInfo))
        {
            return known.Any(value => value is not null && isCopiedMember(value));
        }

        if (descriptor && framework)
        {
            return Enumerable.Range(1, method.GetParameters().Length + (method.IsStatic ? 0 : 1))
                .Any(index => HasCopiedMetadata(body, position, index, isCopiedMember));
        }

        if (CopiedMethodReturn(method, isCopiedMember))
        {
            return true;
        }

        if (IndirectMetadataTargets(body, position, method) is { } targets
            && targets.Any(target => CopiedMethodReturn(target, isCopiedMember)))
        {
            return true;
        }

        return false;
    }

    private bool CopiedMethodReturn(MethodBase method, Func<object, bool> isCopiedMember)
        => bodies.TryGetValue(IlAsmRenderer.DefinitionOf(method), out var called) && called is not null
            && called.State.Entries.Select((entry, index) => (entry.Instruction, index)).Any(entry =>
                entry.Instruction?.Op == OpCodes.Ret && HasCopiedMetadata(called, entry.index, 1, isCopiedMember));

    private bool CopiedMetadataParameter(MethodEditBody body, int argument, Func<object, bool> isCopiedMember)
    {
        if (CopiedAddressWrites((body.Method, argument, true), isCopiedMember))
        {
            return true;
        }

        if (body.State.Entries.Select((entry, index) => (entry.Instruction, index)).Any(entry =>
            entry.Instruction?.ArgumentIndex == argument
                && entry.Instruction.Op.Name?.StartsWith("starg", StringComparison.Ordinal) == true
                && HasCopiedMetadata(body, entry.index, 1, isCopiedMember)))
        {
            return true;
        }

        var parameter = argument - (body.Method.IsStatic ? 0 : 1);
        foreach (var caller in bodies.Values.OfType<MethodEditBody>())
        {
            for (var position = 0; position < caller.State.Entries.Count; position++)
            {
                if (caller.State.Entries[position].Instruction is not { Operand: ResolvedMethod resolved } instruction
                    || instruction.Op != OpCodes.Call && instruction.Op != OpCodes.Callvirt && instruction.Op != OpCodes.Newobj)
                {
                    continue;
                }

                var method = resolveMethod(resolved);
                if (IlAsmRenderer.DefinitionOf(method) == body.Method)
                {
                    if (instruction.Op == OpCodes.Newobj && parameter < 0)
                    {
                        continue;
                    }

                    if (HasCopiedMetadata(caller, position, method.GetParameters().Length - parameter, isCopiedMember))
                    {
                        return true;
                    }
                }
                else if (IndirectMetadataTargets(caller, position, method)?.Any(target =>
                    IlAsmRenderer.DefinitionOf(target) == body.Method) == true
                    && CopiedIndirectArguments(caller, position, method, isCopiedMember))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private MethodBase[]? IndirectMetadataTargets(MethodEditBody body, int position, MethodBase method)
    {
        var owner = method.DeclaringType;
        object?[]? values;
        if (owner is not null && typeof(Delegate).IsAssignableFrom(owner) && method.IsConstructor)
        {
            values = Stack(body, position, 1);
        }
        else if (owner is not null && typeof(Delegate).IsAssignableFrom(owner) && method.Name is "Invoke" or "DynamicInvoke")
        {
            values = Argument(body, position, -1);
        }
        else if (owner?.Assembly != typeof(Type).Assembly)
        {
            return null;
        }
        else if (owner == typeof(Delegate) && method.Name == nameof(Delegate.CreateDelegate))
        {
            values = DelegateTargets(body, position, method);
        }
        else if ((typeof(Type).IsAssignableFrom(owner) || owner == typeof(IReflect)) && method.Name == nameof(Type.InvokeMember))
        {
            values = NamedMembers(body, position, -1, 0);
        }
        else if (method.Name is "Invoke" or "GetValue" or "CreateDelegate"
            && (typeof(MethodBase).IsAssignableFrom(owner) || typeof(PropertyInfo).IsAssignableFrom(owner)
                || owner == typeof(MethodInvoker) || owner == typeof(ConstructorInvoker)))
        {
            values = Argument(body, position, -1);
        }
        else
        {
            return null;
        }

        return values?.Select(value => value is PropertyInfo property ? property.GetMethod : value as MethodBase)
            .OfType<MethodBase>().Distinct().ToArray();
    }

    private bool CopiedIndirectArguments(MethodEditBody body, int position, MethodBase method, Func<object, bool> isCopiedMember)
    {
        var parameters = method.GetParameters();
        var invocation = method.Name == "Invoke" && typeof(Delegate).IsAssignableFrom(method.DeclaringType);
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index].ParameterType;
            if (!invocation && parameter != typeof(object) && parameter != typeof(object[]) && !parameter.IsByRef)
            {
                continue;
            }

            if (HasCopiedMetadata(body, position, parameters.Length - index, isCopiedMember))
            {
                return true;
            }
        }

        return false;
    }
}
