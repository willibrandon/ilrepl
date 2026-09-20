using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Tracks metadata written through addresses of locals, arguments and fields, including aliases passed through copied helpers.
/// </summary>
internal sealed partial class ReflectionValueResolver
{
    private readonly HashSet<(MethodBase Method, int Origin)> _metadataAddressActive = [];
    private readonly HashSet<(MethodBase Method, int Origin)> _metadataAddressVisited = [];
    private readonly Dictionary<(MethodBase Method, int Position, int FromTop), (HashSet<object> Slots, bool Unknown)>
        _metadataAddressQueries = [];

    /// <summary>
    /// Identifies fields, unknown addresses and external array storage receiving the value of an indirect store.
    /// </summary>
    internal (FieldInfo[] Fields, bool Unknown, bool ExternalArray) MetadataStoreTargets(MethodEditBody body, int position)
    {
        var address = MetadataAddressAt(body, position, 2);
        var externalArray = address.Slots.OfType<(MethodEditBody Body, int Position)>()
            .Any(element => HasExternalArrayStorage(element.Body, element.Position, 2));
        return (address.Slots.OfType<FieldInfo>().ToArray(), address.Unknown, externalArray);
    }

    /// <summary>
    /// Reports arrays whose storage is not proved to originate entirely from allocations inside the copied family.
    /// </summary>
    internal bool HasExternalArrayStorage(MethodEditBody body, int position, int fromTop)
    {
        var array = MetadataArrayAt(body, position, fromTop);
        return array.Unknown || array.Sites.Count == 0;
    }

    private bool CopiedAddressWrites(object slot, Func<object, bool> isCopiedMember)
    {
        foreach (var body in bodies.Values.OfType<MethodEditBody>())
        {
            for (var position = 0; position < body.State.Entries.Count; position++)
            {
                var instruction = body.State.Entries[position].Instruction;
                if (instruction is null || instruction.Op != OpCodes.Stobj && instruction.Op != OpCodes.Cpobj
                    && instruction.Op.Name?.StartsWith("stind.", StringComparison.Ordinal) != true)
                {
                    continue;
                }

                var destination = MetadataAddressAt(body, position, 2);
                if ((destination.Unknown || destination.Slots.Contains(slot))
                    && HasCopiedMetadata(body, position, 1, isCopiedMember))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private (HashSet<object> Slots, bool Unknown) MetadataAddressAt(MethodEditBody body, int position, int fromTop)
    {
        var key = (body.Method, position, fromTop);
        if (_metadataAddressQueries.TryGetValue(key, out var known))
        {
            return known;
        }

        _metadataAddressVisited.Clear();
        var slots = new HashSet<object>();
        var unknown = false;
        AddMetadataAddressStack(body, position, fromTop, slots, ref unknown);
        var result = (slots, unknown);
        _metadataAddressQueries.Add(key, result);
        return result;
    }

    private void AddMetadataAddressStack(MethodEditBody body, int position, int fromTop, HashSet<object> slots, ref bool unknown)
    {
        var values = body.State.Analysis.Before[position]?.Values;
        if (values is null || values.Length < fromTop)
        {
            unknown = true;
            return;
        }

        var value = values[^fromTop];
        if (value.Type is { } type && !type.IsByRef && !type.IsPointer && type != typeof(nint) && type != typeof(nuint))
        {
            return;
        }

        if (value.Origins.Length == 0)
        {
            unknown = true;
        }

        foreach (var origin in value.Origins)
        {
            AddMetadataAddressOrigin(body, origin, slots, ref unknown);
        }
    }

    private void AddMetadataAddressOrigin(MethodEditBody body, int position, HashSet<object> slots, ref bool unknown)
    {
        if (!_metadataAddressVisited.Add((body.Method, position)))
        {
            return;
        }

        if (_metadataAddressActive.Count >= Limit || slots.Count >= Limit)
        {
            unknown = true;
            return;
        }

        if (!_metadataAddressActive.Add((body.Method, position)))
        {
            return;
        }

        try
        {
            var instruction = body.State.Entries[position].Instruction;
            if (instruction is null)
            {
                unknown = true;
                return;
            }

            var op = instruction.Op;
            if (instruction.LocalIndex is { } local && op.Name?.StartsWith("ldloca", StringComparison.Ordinal) == true)
            {
                slots.Add((body.Method, local, false));
                return;
            }

            if (instruction.ArgumentIndex is { } address && op.Name?.StartsWith("ldarga", StringComparison.Ordinal) == true)
            {
                slots.Add((body.Method, address, true));
                return;
            }

            if (instruction.Operand is FieldInfo field && (op == OpCodes.Ldflda || op == OpCodes.Ldsflda))
            {
                slots.Add(field);
                return;
            }

            if (op == OpCodes.Ldelema)
            {
                slots.Add((body, position));
                return;
            }

            if (op == OpCodes.Ldnull)
            {
                return;
            }

            if (op == OpCodes.Conv_I || op == OpCodes.Conv_U || op == OpCodes.Castclass || op == OpCodes.Ldobj
                || op == OpCodes.Ldind_I || op == OpCodes.Ldind_Ref)
            {
                AddMetadataAddressStack(body, position, 1, slots, ref unknown);
                return;
            }

            if (instruction.LocalIndex is { } alias && op.Name?.StartsWith("ldloc", StringComparison.Ordinal) == true)
            {
                var found = false;
                foreach (var entry in body.State.Entries.Select((entry, index) => (entry.Instruction, index)))
                {
                    if (entry.Instruction?.LocalIndex != alias
                        || entry.Instruction.Op.Name?.StartsWith("stloc", StringComparison.Ordinal) != true)
                    {
                        continue;
                    }

                    found = true;
                    AddMetadataAddressStack(body, entry.index, 1, slots, ref unknown);
                }

                if (!found)
                {
                    unknown = true;
                }

                return;
            }

            if (instruction.ArgumentIndex is { } argument && op.Name?.StartsWith("ldarg", StringComparison.Ordinal) == true)
            {
                AddMetadataAddressParameter(body, argument, slots, ref unknown);
                return;
            }

            if (instruction.Operand is FieldInfo storedField && (op == OpCodes.Ldfld || op == OpCodes.Ldsfld))
            {
                var found = false;
                foreach (var candidate in bodies.Values.OfType<MethodEditBody>())
                {
                    foreach (var entry in candidate.State.Entries.Select((entry, index) => (entry.Instruction, index)))
                {
                    if (entry.Instruction?.Operand is not FieldInfo written || written != storedField
                        || entry.Instruction.Op != OpCodes.Stfld && entry.Instruction.Op != OpCodes.Stsfld)
                        {
                            continue;
                        }

                        found = true;
                    AddMetadataAddressStack(candidate, entry.index, 1, slots, ref unknown);
                    }
                }

                if (!found)
                {
                    unknown = true;
                }

                return;
            }

            if (instruction.Operand is ResolvedMethod resolved && (op == OpCodes.Call || op == OpCodes.Callvirt))
            {
                var method = resolveMethod(resolved);
                if (bodies.TryGetValue(IlAsmRenderer.DefinitionOf(method), out var helper) && helper is not null)
                {
                    foreach (var entry in helper.State.Entries.Select((entry, index) => (entry.Instruction, index))
                        .Where(entry => entry.Instruction?.Op == OpCodes.Ret))
                    {
                        AddMetadataAddressStack(helper, entry.index, 1, slots, ref unknown);
                    }

                    return;
                }
            }

            unknown = true;
        }
        finally
        {
            _metadataAddressActive.Remove((body.Method, position));
        }
    }

    private void AddMetadataAddressParameter(MethodEditBody body, int argument, HashSet<object> slots, ref bool unknown)
    {
        var found = false;
        foreach (var entry in body.State.Entries.Select((entry, index) => (entry.Instruction, index)))
        {
            if (entry.Instruction?.ArgumentIndex != argument
                || entry.Instruction.Op.Name?.StartsWith("starg", StringComparison.Ordinal) != true)
            {
                continue;
            }

            found = true;
            AddMetadataAddressStack(body, entry.index, 1, slots, ref unknown);
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

                var called = resolveMethod(resolved);
                if (IlAsmRenderer.DefinitionOf(called) == body.Method)
                {
                    if (instruction.Op == OpCodes.Newobj && parameter < 0)
                    {
                        continue;
                    }

                    found = true;
                    AddMetadataAddressStack(caller, position, called.GetParameters().Length - parameter, slots, ref unknown);
                }
                else if (IndirectMetadataTargets(caller, position, called)?.Any(target =>
                    IlAsmRenderer.DefinitionOf(target) == body.Method) == true)
                {
                    unknown = true;
                }
            }
        }

        if (!found)
        {
            unknown = true;
        }
    }
}
