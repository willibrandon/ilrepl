using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Follows allocated array elements through aliases and copied helpers without assuming unknown mutations preserve metadata identity.
/// </summary>
internal sealed partial class ReflectionValueResolver
{
    private readonly HashSet<(MethodBase Method, int Origin)> _arrayActive = [];
    private readonly HashSet<(MethodBase Method, int Origin)> _arrayElementsActive = [];

    private bool ArrayElementMayReferenceMetadata(MethodEditBody body, int position, int fromTop = 2)
    {
        var stack = body.State.Analysis.Before[position]?.Values;
        var element = stack is not null && stack.Length >= fromTop ? stack[^fromTop].Type?.GetElementType() : null;
        if (element is not null && !MayBeMetadata(element)) return false;
        var values = ArrayElementValues(body, position, fromTop);
        return values is null || values.Any(candidate => candidate is Assembly or Module or ModuleHandle
            || candidate is ReflectedInstance instance && MetadataType(instance.Type));
    }

    private static bool MayBeMetadata(Type type) => type == typeof(object) || type.IsInterface || type.IsGenericParameter
        || MetadataType(type);

    private object?[]? ArrayElementValues(MethodEditBody body, int position, int fromTop = 2)
    {
        if (_arrayElementsActive.Count >= Limit || !_arrayElementsActive.Add((body.Method, position))) return null;
        try
        {
            var array = ArrayAt(body, position, fromTop);
            if (array.Unknown) return null;
            var indices = fromTop == 2 ? Stack(body, position, 1) : null;
            var values = new List<object?> { null };
            foreach (var candidate in bodies.Values.OfType<MethodEditBody>())
            {
                for (var index = 0; index < candidate.State.Entries.Count; index++)
                {
                    var instruction = candidate.State.Entries[index].Instruction;
                    if (instruction is null) continue;
                    if (instruction.Op.Name?.StartsWith("stelem", StringComparison.Ordinal) == true)
                    {
                        var stored = ArrayAt(candidate, index, 3);
                        if (!array.Sites.Overlaps(stored.Sites) && !stored.Unknown) continue;
                        var destination = Stack(candidate, index, 2);
                        if (indices is not null && destination is not null
                            && indices.All(value => value is int) && destination.All(value => value is int)
                            && !indices.Intersect(destination).Any()) continue;
                        var incoming = Stack(candidate, index, 1);
                        if (incoming is null)
                        {
                            var type = candidate.State.Analysis.Before[index]?.Values?.LastOrDefault()?.Type;
                            if (type is null || MayBeMetadata(type) || type.IsByRef) return null;
                            incoming = [new ReflectedInstance(type)];
                        }
                        values.AddRange(incoming);
                        if (values.Count > Limit) return null;
                    }
                    else if (instruction.Op == OpCodes.Ldelema && array.Sites.Overlaps(ArrayAt(candidate, index, 2).Sites)) return null;
                    else if (instruction.Operand is ResolvedMethod resolved
                        && (instruction.Op == OpCodes.Call || instruction.Op == OpCodes.Callvirt || instruction.Op == OpCodes.Newobj))
                    {
                        var called = resolveMethod(resolved);
                        if (called.DeclaringType == typeof(Array) && called.Name == nameof(Array.GetValue)) continue;
                        if (bodies.TryGetValue(IlAsmRenderer.DefinitionOf(called), out var helper) && helper is not null) continue;
                        var count = called.GetParameters().Length + (called.IsStatic || instruction.Op == OpCodes.Newobj ? 0 : 1);
                        for (var argument = 1; argument <= count; argument++)
                            if (array.Sites.Overlaps(ArrayAt(candidate, index, argument).Sites)) return null;
                    }
                }
            }
            return values.Distinct().ToArray();
        }
        finally
        {
            _arrayElementsActive.Remove((body.Method, position));
        }
    }

    private (HashSet<(MethodBase Method, int Origin)> Sites, bool Unknown) ArrayAt(MethodEditBody body, int position, int fromTop)
    {
        var sites = new HashSet<(MethodBase Method, int Origin)>();
        var unknown = false;
        AddArrayStack(body, position, fromTop, sites, ref unknown);
        return (sites, unknown);
    }

    private void AddArrayStack(MethodEditBody body, int position, int fromTop,
        HashSet<(MethodBase Method, int Origin)> sites, ref bool unknown)
    {
        var stack = body.State.Analysis.Before[position]?.Values;
        if (stack is null || stack.Length < fromTop)
        {
            unknown = true;
            return;
        }
        var value = stack[^fromTop];
        if (value.Type is { } type && type != typeof(object) && !type.IsArray && !type.IsInterface && type != typeof(Array)
            && !type.IsByRef) return;
        if (value.Origins.Length == 0) unknown = true;
        foreach (var origin in value.Origins) AddArrayOrigin(body, origin, sites, ref unknown);
    }

    private void AddArrayOrigin(MethodEditBody body, int position, HashSet<(MethodBase Method, int Origin)> sites, ref bool unknown)
    {
        if (_arrayActive.Count >= Limit || sites.Count >= Limit || !_arrayActive.Add((body.Method, position)))
        {
            unknown = true;
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
            if (op == OpCodes.Newarr)
            {
                sites.Add((body.Method, position));
                return;
            }
            if (op == OpCodes.Ldnull || op == OpCodes.Ldstr || op == OpCodes.Box) return;
            if (op == OpCodes.Castclass || op == OpCodes.Isinst || op == OpCodes.Ldind_Ref || op == OpCodes.Ldobj)
            {
                AddArrayStack(body, position, 1, sites, ref unknown);
                return;
            }
            if (instruction.LocalIndex is { } local && op.Name?.StartsWith("ldloc", StringComparison.Ordinal) == true)
            {
                var found = false;
                for (var index = 0; index < body.State.Entries.Count; index++)
                {
                    var entry = body.State.Entries[index].Instruction;
                    if (entry?.LocalIndex != local) continue;
                    if (entry.Op.Name?.StartsWith("ldloca", StringComparison.Ordinal) == true) unknown = true;
                    if (entry.Op.Name?.StartsWith("stloc", StringComparison.Ordinal) != true) continue;
                    found = true;
                    AddArrayStack(body, index, 1, sites, ref unknown);
                }
                if (!found) unknown = true;
                return;
            }
            if (instruction.Operand is FieldInfo field
                && (op == OpCodes.Ldfld || op == OpCodes.Ldsfld || op == OpCodes.Ldflda || op == OpCodes.Ldsflda))
            {
                if (op == OpCodes.Ldflda || op == OpCodes.Ldsflda) unknown = true;
                var found = false;
                foreach (var candidate in bodies.Values.OfType<MethodEditBody>())
                {
                    for (var index = 0; index < candidate.State.Entries.Count; index++)
                    {
                        if (candidate.State.Entries[index].Instruction is not { Operand: FieldInfo stored } entry || stored != field)
                            continue;
                        if (entry.Op != OpCodes.Stfld && entry.Op != OpCodes.Stsfld) continue;
                        found = true;
                        AddArrayStack(candidate, index, 1, sites, ref unknown);
                    }
                }
                if (!found) unknown = true;
                return;
            }
            if (instruction.ArgumentIndex is { } argument && op.Name?.StartsWith("ldarg", StringComparison.Ordinal) == true)
            {
                AddArrayParameter(body, argument, sites, ref unknown);
                return;
            }
            if (instruction.Operand is ResolvedMethod resolved && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj))
            {
                var called = resolveMethod(resolved);
                if (op == OpCodes.Newobj && called.DeclaringType?.IsArray != true) return;
                if (bodies.TryGetValue(IlAsmRenderer.DefinitionOf(called), out var helper) && helper is not null)
                {
                    var found = false;
                    for (var index = 0; index < helper.State.Entries.Count; index++)
                    {
                        if (helper.State.Entries[index].Instruction?.Op != OpCodes.Ret) continue;
                        found = true;
                        AddArrayStack(helper, index, 1, sites, ref unknown);
                    }
                    if (!found) unknown = true;
                    return;
                }
                if (called is MethodInfo info && !info.ReturnType.IsArray && info.ReturnType != typeof(object)
                    && !info.ReturnType.IsInterface && info.ReturnType != typeof(Array)) return;
            }
            unknown = true;
        }
        finally
        {
            _arrayActive.Remove((body.Method, position));
        }
    }

    private void AddArrayParameter(MethodEditBody body, int argument, HashSet<(MethodBase Method, int Origin)> sites, ref bool unknown)
    {
        var found = false;
        if (body.Method == selected) unknown = true;
        for (var index = 0; index < body.State.Entries.Count; index++)
        {
            var instruction = body.State.Entries[index].Instruction;
            if (instruction?.ArgumentIndex != argument) continue;
            if (instruction.Op.Name?.StartsWith("ldarga", StringComparison.Ordinal) == true) unknown = true;
            if (instruction.Op.Name?.StartsWith("starg", StringComparison.Ordinal) != true) continue;
            found = true;
            AddArrayStack(body, index, 1, sites, ref unknown);
        }
        var parameter = argument - (body.Method.IsStatic ? 0 : 1);
        foreach (var caller in bodies.Values.OfType<MethodEditBody>())
        {
            for (var position = 0; position < caller.State.Entries.Count; position++)
            {
                if (caller.State.Entries[position].Instruction is not { Operand: ResolvedMethod resolved } instruction
                    || instruction.Op != OpCodes.Call && instruction.Op != OpCodes.Callvirt && instruction.Op != OpCodes.Newobj) continue;
                var called = resolveMethod(resolved);
                if (instruction.Op == OpCodes.Newobj && parameter < 0 || IlAsmRenderer.DefinitionOf(called) != body.Method) continue;
                found = true;
                AddArrayStack(caller, position, called.GetParameters().Length - parameter, sites, ref unknown);
            }
        }
        if (!found) unknown = true;
    }
}
