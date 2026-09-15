using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Follows copied metadata stored in whole arrays without interpreting the inspected external call as an unknown mutation.
/// </summary>
internal sealed partial class ReflectionValueResolver
{
    private readonly Dictionary<(MethodBase Method, int Position, int FromTop),
        (HashSet<(MethodBase Method, int Origin)> Sites, bool Unknown)> _metadataArrayQueries = [];

    private (HashSet<(MethodBase Method, int Origin)> Sites, bool Unknown) MetadataArrayAt(MethodEditBody body, int position, int fromTop)
    {
        var key = (body.Method, position, fromTop);
        if (_metadataArrayQueries.TryGetValue(key, out var known)) return known;
        var array = ArrayAt(body, position, fromTop);
        _metadataArrayQueries.Add(key, array);
        return array;
    }

    private bool CopiedArrayContents(MethodEditBody body, int position, int? fromTop, Func<object, bool> isCopiedMember,
        bool filterIndex = true)
    {
        var array = fromTop is { } slot ? MetadataArrayAt(body, position, slot)
            : (Sites: new HashSet<(MethodBase Method, int Origin)> { (body.Method, position) }, Unknown: false);
        var indices = filterIndex && fromTop == 2 ? Stack(body, position, 1) : null;
        foreach (var candidate in bodies.Values.OfType<MethodEditBody>())
        {
            for (var index = 0; index < candidate.State.Entries.Count; index++)
            {
                var instruction = candidate.State.Entries[index].Instruction;
                if (instruction is null) continue;
                if (instruction.Op.Name?.StartsWith("stelem", StringComparison.Ordinal) == true)
                {
                    var stored = MetadataArrayAt(candidate, index, 3);
                    if (!array.Sites.Overlaps(stored.Sites) && !array.Unknown && !stored.Unknown) continue;
                    var destination = Stack(candidate, index, 2);
                    if (indices is not null && destination is not null
                        && indices.All(value => value is int) && destination.All(value => value is int)
                        && !indices.Intersect(destination).Any()) continue;
                    if (HasCopiedMetadata(candidate, index, 1, isCopiedMember)) return true;
                }
                else if (instruction.Op == OpCodes.Stind_Ref || instruction.Op == OpCodes.Stobj)
                {
                    var active = new HashSet<(MethodBase Method, int Origin)>();
                    if (CopiedArrayAddress(candidate, index, array.Sites, active)
                        && HasCopiedMetadata(candidate, index, 1, isCopiedMember)) return true;
                }
            }
        }
        return false;
    }

    private bool CopiedArrayAddress(MethodEditBody body, int position, HashSet<(MethodBase Method, int Origin)> sites,
        HashSet<(MethodBase Method, int Origin)> active)
    {
        var scalar = MetadataAddressAt(body, position, 2);
        if (!scalar.Unknown && scalar.Slots.Count != 0 && !scalar.Slots.OfType<(MethodEditBody Body, int Position)>().Any()) return false;
        var values = body.State.Analysis.Before[position]?.Values;
        return values is not null && values.Length >= 2
            && values[^2].Origins.Any(origin => CopiedArrayAddressOrigin(body, origin, sites, active));
    }

    private bool CopiedArrayAddressOrigin(MethodEditBody body, int position, HashSet<(MethodBase Method, int Origin)> sites,
        HashSet<(MethodBase Method, int Origin)> active)
    {
        if (active.Count >= Limit) return true;
        if (!active.Add((body.Method, position))) return false;
        try
        {
            var instruction = body.State.Entries[position].Instruction;
            if (instruction is null) return true;
            if (instruction.Op == OpCodes.Ldelema)
            {
                var array = MetadataArrayAt(body, position, 2);
                return array.Unknown || sites.Overlaps(array.Sites);
            }
            if (instruction.LocalIndex is { } local && instruction.Op.Name?.StartsWith("ldloc", StringComparison.Ordinal) == true)
            {
                foreach (var entry in body.State.Entries.Select((entry, index) => (entry.Instruction, index)))
                {
                    if (entry.Instruction?.LocalIndex != local
                        || entry.Instruction.Op.Name?.StartsWith("stloc", StringComparison.Ordinal) != true) continue;
                    var values = body.State.Analysis.Before[entry.index]?.Values;
                    if (values is not null && values.Length != 0
                        && values[^1].Origins.Any(origin => CopiedArrayAddressOrigin(body, origin, sites, active))) return true;
                }
                return false;
            }
            return instruction.ArgumentIndex is not null || instruction.Operand is ResolvedMethod;
        }
        finally
        {
            active.Remove((body.Method, position));
        }
    }
}
