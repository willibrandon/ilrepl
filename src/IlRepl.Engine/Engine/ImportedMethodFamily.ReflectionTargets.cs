using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Discovers string-only type dependencies and validates the actual members selected by indirect reflection.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private static readonly HashSet<string> IndirectReflectionNames = new[]
    {
        typeof(Assembly), typeof(Module), typeof(Attribute), typeof(CustomAttributeExtensions), typeof(CustomAttributeData),
        typeof(ICustomAttributeProvider), typeof(Type), typeof(Delegate), typeof(MethodBase), typeof(MethodInfo),
        typeof(PropertyInfo), typeof(MethodInvoker), typeof(ConstructorInvoker), typeof(Activator), typeof(RuntimeMethodHandle),
        typeof(ModuleHandle), typeof(object), typeof(RuntimeHelpers),
    }.SelectMany(type => type.GetMethods()).Where(method => AssemblyInspectionProblem(method) is not null || IsIndirectReflection(method)
        || IsTypeLookup(method) || IsActivation(method) || IsAssemblyActivation(method) || IsObjectReferenceInspection(method)
        || IsMemberTokenInspection(method) || IsTypeNameInspection(method))
        .SelectMany(method => method.Name.StartsWith("get_", StringComparison.Ordinal) ? new[] { method.Name, method.Name[4..] }
            : new[] { method.Name }).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private ReflectionValueResolver ReflectionValues() => new(Selected.Method, _methods,
        resolved => resolved.Method ?? _pinned[resolved.Definition!.Name]);

    private void DiscoverTypeLookupTargets()
    {
        var values = ReflectionValues();
        foreach (var body in _methods.Values.OfType<MethodEditBody>().ToArray())
        {
            if (_runtimeHelperTypes.Contains(body.Method.DeclaringType!)) continue;
            for (var position = 0; position < body.State.Entries.Count; position++)
            {
                var instruction = body.State.Entries[position].Instruction;
                if (instruction?.Operand is not ResolvedMethod resolved || instruction.Op != OpCodes.Call
                    && instruction.Op != OpCodes.Callvirt) continue;
                var target = resolved.Method ?? _pinned[resolved.Definition!.Name];
                var activation = IsActivation(target);
                if (!activation && !IsAssemblyActivation(target) && !IsTypeLookup(target)) continue;
                var context = body.Method.Module.Assembly;
                if (target.DeclaringType == typeof(Type) && target.GetParameters().Length >= 3
                    && target.GetParameters()[1].ParameterType != typeof(bool)
                    && values.Argument(body, position, 2) is { Length: > 0 } resolvers && resolvers.All(value => value is not null))
                    continue;
                var sources = _types.Keys.Where(type => !_runtimeHelperTypes.Contains(type))
                    .Select(type => type.Assembly).Append(context).ToHashSet();
                var contexts = new[] { context };
                if (!target.IsStatic)
                {
                    var receivers = values.Argument(body, position, -1);
                    contexts = receivers is null ? sources.ToArray() : receivers.Select(receiver => receiver is Assembly assembly
                            ? assembly : (receiver as Module)?.Assembly).OfType<Assembly>().Where(sources.Contains).Distinct().ToArray();
                }
                else if (activation)
                {
                    var assemblies = values.Argument(body, position, 0);
                    contexts = assemblies is null ? sources.ToArray() : assemblies.Select(assembly => assembly is null or string
                            ? ReflectionValueResolver.ActivationAssembly((string?)assembly, context,
                                target.Name == nameof(Activator.CreateInstanceFrom)) : null)
                        .OfType<Assembly>().Where(sources.Contains).Distinct().ToArray();
                    if (contexts.Length == 0) contexts = sources.ToArray();
                }
                var location = MemberResolver.Describe(body.Method) + ": " + instruction.Text;
                var names = values.Argument(body, position, activation ? 1 : 0);
                foreach (var source in contexts)
                {
                    if (names is null)
                    {
                        Type[] types;
                        try
                        {
                            types = source.GetTypes();
                        }
                        catch (ReflectionTypeLoadException exception)
                        {
                            throw new ReplException(location + ": dynamic type lookup requires the complete source assembly's types",
                                exception);
                        }

                        foreach (var type in types) AddLookupType(type, sources, location);
                    }
                    else
                    {
                        foreach (var name in names.OfType<string>())
                        foreach (var ignoreCase in values.LookupCasing(body, position, target))
                        {
                            var type = ReflectionValueResolver.NamedType(name, source, ignoreCase);
                            if (type is not null) AddLookupType(type, sources, location);
                        }
                    }
                }
            }
        }
    }

    private void AddLookupType(Type type, HashSet<Assembly> sources, string location)
    {
        if (type.HasElementType)
        {
            AddLookupType(type.GetElementType()!, sources, location);
            return;
        }

        if (type.IsGenericParameter) return;
        foreach (var argument in type.IsConstructedGenericType ? type.GetGenericArguments() : Type.EmptyTypes)
            AddLookupType(argument, sources, location);
        type = DefinitionOf(type);
        if (!sources.Contains(type.Assembly) && !TypeRelations.IsSessionType(type) || _runtimeHelperTypes.Contains(type)) return;
        if (_externalTypes.Contains(type))
            throw new ReplException(location + ": string lookup requires copying the externally retained type "
                + TypeNameFormatter.Pretty(type));
        AddType(type);
        _dependencies.Add(new EditDependency(TypeNameFormatter.Pretty(type), type.Assembly.FullName!,
            location + ": string type lookup", "copied"));
    }

    private static bool IsIndirectReflection(MethodBase method)
    {
        var type = method.DeclaringType;
        if (type?.Assembly != typeof(Type).Assembly) return false;
        return typeof(MethodBase).IsAssignableFrom(type) && method.Name is nameof(MethodBase.Invoke) or nameof(MethodInfo.CreateDelegate)
            || typeof(PropertyInfo).IsAssignableFrom(type) && method.Name == nameof(PropertyInfo.GetValue)
            || (typeof(Type).IsAssignableFrom(type) || type == typeof(IReflect)) && method.Name == nameof(Type.InvokeMember)
            || type == typeof(Delegate) && method.Name is nameof(Delegate.CreateDelegate) or nameof(Delegate.DynamicInvoke)
            || type == typeof(RuntimeMethodHandle) && method.Name == nameof(RuntimeMethodHandle.GetFunctionPointer)
            || (type == typeof(MethodInvoker) || type == typeof(ConstructorInvoker))
                && method.Name is nameof(MethodInvoker.Create) or nameof(MethodInvoker.Invoke);
    }

    private void ValidateIndirectReflection()
    {
        var values = ReflectionValues();
        foreach (var body in _methods.Values.OfType<MethodEditBody>())
        {
            if (_runtimeHelperTypes.Contains(body.Method.DeclaringType!)) continue;
            for (var position = 0; position < body.State.Entries.Count; position++)
            {
                var instruction = body.State.Entries[position].Instruction;
                if (instruction is not null) ValidateMetadataReference(values, body, position, instruction);
                if (instruction is not null) ValidateMemberTokenReference(values, body, position, instruction);
                if (instruction is not null) ValidateTypeNameReference(values, body, position, instruction);
                if (instruction?.Operand is not ResolvedMethod resolved || instruction.Op == OpCodes.Ldtoken) continue;
                var target = resolved.Method ?? _pinned[resolved.Definition!.Name];
                if (target.DeclaringType == typeof(object) && target.Name is nameof(ToString) or nameof(Equals) or nameof(GetHashCode)
                    && (instruction.Op == OpCodes.Callvirt || instruction.Op == OpCodes.Ldvirtftn))
                {
                    var dispatched = MetadataIdentityOverride(target, values.Argument(body, position, -1));
                    if (dispatched != target && AssemblyInspectionProblem(dispatched,
                            values.Argument(body, position, -1)) is { } identityProblem)
                        RejectReflection(body, instruction, dispatched, identityProblem);
                }
                if (!IsIndirectReflection(target)) continue;
                var candidates = target.Name == nameof(Type.InvokeMember) ? values.NamedMembers(body, position, -1, 0)
                    : target.DeclaringType == typeof(Delegate) && target.Name == nameof(Delegate.CreateDelegate)
                        ? values.DelegateTargets(body, position, target)
                        : values.Argument(body, position, target.IsStatic ? 0 : -1);
                var reason = candidates is null ? "indirect reflection cannot prove a supported target" : null;
                var selected = target;
                foreach (var candidate in candidates ?? [])
                {
                    if (candidate is ReflectedDelegateName name && !IndirectReflectionNames.Contains(name.Name)) continue;
                    var member = candidate is PropertyInfo property ? property.GetMethod : candidate as MethodBase;
                    // A delegate's bound receiver was validated when its binding or method pointer was created.
                    if (member?.DeclaringType == typeof(object) && !member.IsStatic
                        && member.Name is nameof(ToString) or nameof(Equals) or nameof(GetHashCode)
                        && target.Name != nameof(Delegate.DynamicInvoke))
                    {
                        var receivers = InvocationReceiver(values, body, position, target);
                        if (receivers is null || target.Name == nameof(Delegate.CreateDelegate)
                            && receivers.Any(receiver => receiver is null))
                            reason = "indirect reflection cannot prove a supported target";
                        member = MetadataIdentityOverride(member, receivers);
                    }
                    if (member is not null && member.IsStatic && IsObjectReferenceInspection(member))
                        reason = "indirect reflection cannot prove a supported target";
                    if (candidate is null) continue;
                    if (candidate is FieldInfo) continue;
                    if (member is null || IsIndirectReflection(member))
                        reason = "indirect reflection cannot prove a supported target";
                    else if (IsMemberTokenInspection(member) && target.Name == nameof(Delegate.DynamicInvoke)) continue;
                    else if (AssemblyInspectionProblem(member, MemberTokenReceiver(values, body, position, target)) is { } problem)
                    {
                        reason = problem;
                        selected = member;
                        break;
                    }
                    else if (IsTypeLookup(member) || IsActivation(member) || IsAssemblyActivation(member))
                    {
                        reason = "indirect type lookup cannot translate copied names";
                        selected = member;
                        break;
                    }
                }

                if (reason is null) continue;
                RejectReflection(body, instruction, selected, reason);
            }
        }
    }

    private static MethodBase MetadataIdentityOverride(MethodBase method, object?[]? receivers)
    {
        if (method.DeclaringType != typeof(object) || method.IsStatic
            || method.Name is not (nameof(ToString) or nameof(Equals) or nameof(GetHashCode))) return method;
        foreach (var type in new[] { typeof(Assembly), typeof(Module), typeof(ModuleHandle), typeof(Type) })
        {
            if (receivers?.Any(receiver => type.IsInstanceOfType(receiver)
                || receiver is ReflectedInstance instance && type.IsAssignableFrom(instance.Type)) == true)
                return type.GetMethod(method.Name, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray())!;
        }
        return method;
    }

    private static object?[]? InvocationReceiver(ReflectionValueResolver values, MethodEditBody body, int position, MethodBase target)
    {
        if (target.Name == nameof(MethodBase.Invoke)
            && (target.DeclaringType == typeof(MethodInvoker) || typeof(MethodBase).IsAssignableFrom(target.DeclaringType!)))
            return values.Argument(body, position, 0);
        if (target.Name == nameof(Delegate.CreateDelegate)
            && (target.DeclaringType == typeof(Delegate) || typeof(MethodInfo).IsAssignableFrom(target.DeclaringType!)))
        {
            var parameters = target.GetParameters();
            var index = Array.FindIndex(parameters, parameter => parameter.ParameterType == typeof(object));
            if (index >= 0) return values.Argument(body, position, index);
        }
        return null;
    }

    private void RejectReflection(MethodEditBody body, Instruction instruction, MethodBase target, string reason)
    {
        var location = MemberResolver.Describe(body.Method) + ": " + instruction.Text;
        _dependencies.Add(new EditDependency(MemberResolver.Describe(target), target.Module.Assembly.FullName!, location,
            "blocked: " + reason) { Access = MemberAccess.AccessWord(target.Attributes) });
        throw new ReplException(location + ": " + MemberResolver.Describe(target) + ": " + reason);
    }
}
