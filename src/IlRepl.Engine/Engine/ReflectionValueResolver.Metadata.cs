using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Follows potential metadata references without losing known producers when another incoming value is unknown.
/// </summary>
internal sealed partial class ReflectionValueResolver
{
    private readonly HashSet<(MethodBase Method, int Origin)> _metadataActive = [];

    /// <summary>
    /// Finds assembly and module references through stack values, mutable storage and copied helper methods.
    /// </summary>
    internal bool HasMetadataReference(MethodEditBody body, int position, int fromTop)
    {
        var values = body.State.Analysis.Before[position]?.Values;
        if (values is null || values.Length < fromTop)
        {
            return false;
        }

        var value = values[^fromTop];
        var type = value.Type?.IsByRef == true ? value.Type.GetElementType() : value.Type;
        if (type?.IsValueType == true && type != typeof(ModuleHandle))
        {
            return false;
        }

        return MetadataType(type)
            || value.Origins.Any(origin => MetadataOrigin(body, origin));
    }

    private static bool MetadataType(Type? type) => type is not null && (typeof(Assembly).IsAssignableFrom(type)
        || typeof(Module).IsAssignableFrom(type) || type == typeof(ModuleHandle));

    private bool MetadataOrigin(MethodEditBody body, int position)
    {
        if (_metadataActive.Count >= Limit)
        {
            return true;
        }

        if (!_metadataActive.Add((body.Method, position)))
        {
            return false;
        }

        try
        {
            if (Origin(body, position)?.Any(candidate => candidate is Assembly or Module or ModuleHandle
                || candidate is ReflectedInstance instance && MetadataType(instance.Type)) == true)
            {
                return true;
            }

            var instruction = body.State.Entries[position].Instruction;
            if (instruction is null)
            {
                return false;
            }

            var op = instruction.Op;
            if (op.Name?.StartsWith("ldelem", StringComparison.Ordinal) == true)
            {
                return ArrayElementMayReferenceMetadata(body, position);
            }

            if (op == OpCodes.Castclass || op == OpCodes.Isinst || op == OpCodes.Ldind_Ref || op == OpCodes.Ldobj
                || op == OpCodes.Box || op == OpCodes.Unbox_Any)
            {
                return HasMetadataReference(body, position, 1);
            }

            if (instruction.LocalIndex is { } local && op.Name?.StartsWith("ldloc", StringComparison.Ordinal) == true)
            {
                return body.State.Entries.Select((entry, index) => (entry.Instruction, index)).Any(entry =>
                    entry.Instruction?.LocalIndex == local
                        && entry.Instruction.Op.Name?.StartsWith("stloc", StringComparison.Ordinal) == true
                        && HasMetadataReference(body, entry.index, 1));
            }

            if (instruction.Operand is FieldInfo field && (op == OpCodes.Ldfld || op == OpCodes.Ldsfld
                || op == OpCodes.Ldflda || op == OpCodes.Ldsflda))
            {
                return MetadataType(field.FieldType) || bodies.Values.OfType<MethodEditBody>().Any(candidate =>
                    candidate.State.Entries.Select((entry, index) => (entry.Instruction, index)).Any(entry =>
                        entry.Instruction?.Operand is FieldInfo stored && stored == field
                            && (entry.Instruction.Op == OpCodes.Stfld || entry.Instruction.Op == OpCodes.Stsfld)
                            && HasMetadataReference(candidate, entry.index, 1)));
            }

            if (instruction.ArgumentIndex is { } argument && op.Name?.StartsWith("ldarg", StringComparison.Ordinal) == true)
            {
                return MetadataParameter(body, argument);
            }

            if (instruction.Operand is ResolvedMethod resolved && (op == OpCodes.Call || op == OpCodes.Callvirt))
            {
                var method = resolveMethod(resolved);
                if (method.DeclaringType == typeof(Array) && method.Name == nameof(Array.GetValue))
                {
                    return ArrayElementMayReferenceMetadata(body, position, method.GetParameters().Length + 1);
                }

                if (method is MethodInfo info && MetadataType(info.ReturnType))
                {
                    return true;
                }

                return bodies.TryGetValue(IlAsmRenderer.DefinitionOf(method), out var called) && called is not null
                    && called.State.Entries.Select((entry, index) => (entry.Instruction, index)).Any(entry =>
                        entry.Instruction?.Op == OpCodes.Ret && HasMetadataReference(called, entry.index, 1));
            }

            return false;
        }
        finally
        {
            _metadataActive.Remove((body.Method, position));
        }
    }

    private bool MetadataParameter(MethodEditBody body, int argument)
    {
        if (body.State.Entries.Select((entry, index) => (entry.Instruction, index)).Any(entry =>
            entry.Instruction?.ArgumentIndex == argument && entry.Instruction.Op.Name?.StartsWith("starg", StringComparison.Ordinal) == true
                && HasMetadataReference(body, entry.index, 1)))
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
                if (instruction.Op == OpCodes.Newobj && parameter < 0)
                {
                    continue;
                }

                if (IlAsmRenderer.DefinitionOf(method) == body.Method
                    && HasMetadataReference(caller, position, method.GetParameters().Length - parameter))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
