using System.Reflection;
using System.Reflection.Emit;
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
    }.SelectMany(type => type.GetMethods()).Where(method => AssemblyInspectionProblem(method) is not null || IsIndirectReflection(method)
        || IsTypeLookup(method) || IsActivation(method) || IsAssemblyActivation(method))
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
                if (instruction?.Operand is not ResolvedMethod resolved || instruction.Op == OpCodes.Ldtoken) continue;
                var target = resolved.Method ?? _pinned[resolved.Definition!.Name];
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
                    if (candidate is null) continue;
                    if (candidate is FieldInfo) continue;
                    if (member is null || IsIndirectReflection(member))
                        reason = "indirect reflection cannot prove a supported target";
                    else if (AssemblyInspectionProblem(member) is { } problem)
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
                var location = MemberResolver.Describe(body.Method) + ": " + instruction.Text;
                _dependencies.Add(new EditDependency(MemberResolver.Describe(selected), selected.Module.Assembly.FullName!, location,
                    "blocked: " + reason) { Access = MemberAccess.AccessWord(selected.Attributes) });
                throw new ReplException(location + ": " + MemberResolver.Describe(selected) + ": " + reason);
            }
        }
    }
}
