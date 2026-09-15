using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Keeps copied metadata identities from escaping into external code that can inspect their original context.
/// </summary>
internal sealed partial class ImportedMethodFamily
{
    private const string MetadataBoundaryReason = "external code cannot preserve copied metadata identity";

    private void ValidateMetadataBoundaries()
    {
        var values = ReflectionValues();
        foreach (var body in _methods.Values.OfType<MethodEditBody>())
        {
            if (_runtimeHelperTypes.Contains(body.Method.DeclaringType!)) continue;
            for (var position = 0; position < body.State.Entries.Count; position++)
            {
                var instruction = body.State.Entries[position].Instruction;
                if (instruction is null) continue;
                if (instruction.Op.Name?.StartsWith("stelem", StringComparison.Ordinal) == true
                    && values.HasExternalArrayStorage(body, position, 3)
                    && values.HasCopiedMetadata(body, position, 1, IsCopiedMetadataMember))
                    RejectMetadataBoundary(body, instruction, "array store", body.Method.Module.Assembly);
                if ((instruction.Op == OpCodes.Stobj || instruction.Op == OpCodes.Cpobj
                        || instruction.Op.Name?.StartsWith("stind.", StringComparison.Ordinal) == true)
                    && values.HasCopiedMetadata(body, position, 1, IsCopiedMetadataMember))
                {
                    var storage = values.MetadataStoreTargets(body, position);
                    var externalField = storage.Fields.FirstOrDefault(candidate => candidate.DeclaringType is null
                        || !_types.ContainsKey(DefinitionOf(candidate.DeclaringType)));
                    if (externalField is not null || storage.Unknown || storage.ExternalArray)
                        RejectMetadataBoundary(body, instruction, externalField?.ToString()
                            ?? (storage.ExternalArray ? "array store" : "indirect store"),
                            externalField?.Module.Assembly ?? body.Method.Module.Assembly);
                }
                if (instruction.Operand is FieldInfo field && (instruction.Op == OpCodes.Stfld || instruction.Op == OpCodes.Stsfld)
                    && (field.DeclaringType is null || !_types.ContainsKey(DefinitionOf(field.DeclaringType)))
                    && values.HasCopiedMetadata(body, position, 1, IsCopiedMetadataMember))
                    RejectMetadataBoundary(body, instruction, field.ToString()!, field.Module.Assembly);
                if (instruction.Operand is CalliSignature signature && instruction.Op == OpCodes.Calli)
                {
                    var count = signature.ArgumentPopCount;
                    for (var argument = 2; argument <= count + 1; argument++)
                        if (values.HasCopiedMetadata(body, position, argument, IsCopiedMetadataMember))
                            RejectMetadataBoundary(body, instruction, "indirect call", body.Method.Module.Assembly);
                }
                if (instruction.Operand is not ResolvedMethod resolved
                    || instruction.Op != OpCodes.Call && instruction.Op != OpCodes.Callvirt && instruction.Op != OpCodes.Newobj) continue;
                var target = resolved.Method ?? _pinned[resolved.Definition!.Name];
                if (_methods.ContainsKey(IlAsmRenderer.DefinitionOf(target))) continue;
                if (IsReflectiveMetadataPayload(target))
                {
                    ValidateMetadataPayload(values, body, position, instruction, target, target);
                    continue;
                }
                if (IsIndirectReflection(target))
                {
                    ValidateIndirectMetadataBoundary(values, body, position, instruction, target);
                    continue;
                }
                if (IsMetadataIntrinsic(target)) continue;
                var arguments = target.GetParameters().Length + (target.IsStatic || instruction.Op == OpCodes.Newobj ? 0 : 1);
                for (var argument = 1; argument <= arguments; argument++)
                    if (values.HasCopiedMetadata(body, position, argument, IsCopiedMetadataMember))
                        RejectMetadataBoundary(body, instruction, MemberResolver.Describe(target), target.Module.Assembly);
            }
        }
    }

    private void ValidateIndirectMetadataBoundary(ReflectionValueResolver values, MethodEditBody body, int position,
        Instruction instruction, MethodBase target)
    {
        var candidates = target.Name == nameof(Type.InvokeMember) ? values.NamedMembers(body, position, -1, 0)
            : target.DeclaringType == typeof(Delegate) && target.Name == nameof(Delegate.CreateDelegate)
                ? values.DelegateTargets(body, position, target) : values.Argument(body, position, target.IsStatic ? 0 : -1);
        if (candidates?.Any(candidate => candidate is FieldInfo) == true)
            ValidateMetadataPayload(values, body, position, instruction, target, target);
        var external = candidates?.Select(candidate => candidate is PropertyInfo property ? property.GetMethod : candidate as MethodBase)
            .FirstOrDefault(candidate => candidate is not null && !_methods.ContainsKey(IlAsmRenderer.DefinitionOf(candidate))
                && !IsMetadataIntrinsic(candidate));
        if (external is null) return;
        ValidateMetadataPayload(values, body, position, instruction, target, external);
    }

    private void ValidateMetadataPayload(ReflectionValueResolver values, MethodEditBody body, int position,
        Instruction instruction, MethodBase target, MethodBase external)
    {
        var parameters = target.GetParameters();
        for (var argument = 0; argument < parameters.Length; argument++)
        {
            var parameter = parameters[argument].ParameterType;
            if (parameter != typeof(object) && parameter != typeof(object[])) continue;
            if (values.HasCopiedMetadata(body, position, parameters.Length - argument, IsCopiedMetadataMember))
                RejectMetadataBoundary(body, instruction, MemberResolver.Describe(external), external.Module.Assembly);
        }
    }

    private static bool IsReflectiveMetadataPayload(MethodBase method) => method.DeclaringType is { } type
        && type.Assembly == typeof(Type).Assembly
        && (typeof(FieldInfo).IsAssignableFrom(type) && method.Name == nameof(FieldInfo.SetValue)
            || type == typeof(Activator) && method.Name is nameof(Activator.CreateInstance) or nameof(Activator.CreateInstanceFrom)
            || typeof(Assembly).IsAssignableFrom(type) && method.Name == nameof(Assembly.CreateInstance));

    private bool IsCopiedMetadataMember(object value)
    {
        if (value is ParameterInfo parameter) return IsCopiedMetadataMember(parameter.Member);
        if (value is Type type)
        {
            if (!type.IsGenericParameter) return ContainsCopiedType(type);
            return type.DeclaringMethod is { } method && IsCopiedMetadataMember(method)
                || type.DeclaringType is { } owner && ContainsCopiedType(owner);
        }
        if (value is not MemberInfo member) return false;
        return member.DeclaringType is { } declaring && ContainsCopiedType(declaring)
            || member.ReflectedType is { } reflected && ContainsCopiedType(reflected)
            || member is MethodBase called && _methods.ContainsKey(IlAsmRenderer.DefinitionOf(called));
    }

    private static bool IsMetadataIntrinsic(MethodBase method)
    {
        var type = method.DeclaringType;
        if (type is null) return false;
        if (type == typeof(Enumerable))
            return method.Name is nameof(Enumerable.First) or nameof(Enumerable.FirstOrDefault) or nameof(Enumerable.Single)
                or nameof(Enumerable.SingleOrDefault) or nameof(Enumerable.ElementAt) or nameof(Enumerable.ElementAtOrDefault)
                or nameof(Enumerable.ToArray)
                && !method.GetParameters().Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
        if (type.Assembly != typeof(object).Assembly) return false;
        if (IsIndirectReflection(method)) return false;
        return typeof(MemberInfo).IsAssignableFrom(type) || typeof(ParameterInfo).IsAssignableFrom(type)
            || typeof(Assembly).IsAssignableFrom(type) || typeof(Module).IsAssignableFrom(type)
            || type == typeof(ModuleHandle) || type == typeof(IntrospectionExtensions)
            || type == typeof(RuntimeReflectionExtensions) || type == typeof(CustomAttributeExtensions)
            || type == typeof(CustomAttributeData) || type == typeof(Attribute) || type == typeof(Activator)
            || type == typeof(Enum) || type == typeof(GC) && method.Name == nameof(GC.KeepAlive)
            || type == typeof(object) && method.Name == nameof(GetType)
            || type == typeof(Array) && method.Name == nameof(Array.GetValue)
            || type == typeof(RuntimeHelpers)
                && method.Name is nameof(RuntimeHelpers.GetUninitializedObject) or nameof(RuntimeHelpers.InitializeArray)
            || IsObjectReferenceInspection(method);
    }

    private void RejectMetadataBoundary(MethodEditBody body, Instruction instruction, string target, Assembly assembly)
    {
        var location = MemberResolver.Describe(body.Method) + ": " + instruction.Text;
        _dependencies.Add(new EditDependency(target, assembly.FullName!, location, "blocked: " + MetadataBoundaryReason));
        throw new ReplException(location + ": " + target + ": " + MetadataBoundaryReason);
    }
}
