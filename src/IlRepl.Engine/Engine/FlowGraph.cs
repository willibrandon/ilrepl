using System.Reflection.Emit;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Connects source entries using the same implicit transitions as structured IL emission.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
internal sealed class FlowGraph<T> where T : class
{
    /// <summary>
    /// The method name used in explanations, independent of a submitting document's source identity.
    /// </summary>
    public string BodyName { get; init; } = "cell";

    /// <summary>
    /// The source entries, excluding the synthetic end node.
    /// </summary>
    public IReadOnlyList<FlowNode<T>> Nodes { get; }

    /// <summary>
    /// Successors for every source entry and the synthetic end node.
    /// </summary>
    public List<FlowEdge>[] Edges { get; }

    /// <summary>
    /// The prescribed entry stacks for the method and its handlers.
    /// </summary>
    public Dictionary<int, FlowState<T>> Seeds { get; } = [];

    /// <summary>
    /// The enclosing region sections at each source entry.
    /// </summary>
    public int[][] Regions { get; }

    /// <summary>
    /// Region-section identities used to validate transfers across protected boundaries.
    /// </summary>
    public Dictionary<int, FlowRegion> Sections { get; } = [];

    /// <summary>
    /// Exception clauses in source or metadata dispatch order.
    /// </summary>
    public List<FlowClause> Clauses { get; } = [];

    /// <summary>
    /// Findings established while connecting the body.
    /// </summary>
    public List<AnalysisDiagnostic> Diagnostics { get; } = [];

    /// <summary>
    /// Builds a graph using the supplied exception-object type.
    /// </summary>
    /// <param name="nodes">The source entries.</param>
    /// <param name="objectType">The type pushed when a catch type is omitted.</param>
    /// <param name="complete">Whether undefined targets are final errors.</param>
    /// <param name="hasThis">Whether argument zero begins as the original receiver.</param>
    public FlowGraph(IReadOnlyList<FlowNode<T>> nodes, T objectType, bool complete = false, bool hasThis = false)
    {
        Nodes = nodes;
        Edges = Enumerable.Range(0, nodes.Count + 1).Select(_ => new List<FlowEdge>()).ToArray();
        Regions = new int[nodes.Count + 1][];
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        var sections = new List<int>();
        var groups = new List<int>();
        var ends = new Dictionary<int, int>();
        var groupAt = new Dictionary<int, int>();
        FlowState<T> Entry(FlowValue<T>[] values) => values.Length == 0
            ? hasThis ? FlowState<T>.ThisEntry : FlowState<T>.Empty
            : new FlowState<T>(values, ThisArgumentIsOriginal: hasThis);
        Seeds[0] = Entry([]);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            foreach (var label in node.Labels)
            {
                if (!labels.TryAdd(label, i))
                {
                    Report(i, "FLOW001", AnalysisDiagnosticKind.Error, $"label '{label}' is already defined");
                }
            }

            if (node.Block == BlockKind.Try)
            {
                groups.Add(i);
                sections.Add(i);
                Sections[i] = new FlowRegion(BlockKind.Try, i, nodes.Count, i);
            }
            else if (node.Block is { } kind && groups.Count > 0)
            {
                groupAt[i] = groups[^1];
                var previous = sections[^1];
                Sections[previous] = Sections[previous] with { End = i };
                if (kind == BlockKind.End)
                {
                    ends[groups[^1]] = i;
                    groups.RemoveAt(groups.Count - 1);
                    sections.RemoveAt(sections.Count - 1);
                }
                else
                {
                    sections[^1] = i;
                    Sections[i] = new FlowRegion(kind, i, nodes.Count, groups[^1]);
                    var handlerEntry = kind is BlockKind.Catch or BlockKind.Filter or BlockKind.FilterHandler
                        ? Entry([new FlowValue<T>(node.CatchType ?? objectType, [i])]) : Entry([]);
                    Seeds[i] = new ExceptionalFlowState<T>(handlerEntry, null, i);
                    if (kind == BlockKind.FilterHandler)
                    {
                        var filter = Clauses.FindLastIndex(clause => clause.Group == groups[^1]
                            && clause.Kind == BlockKind.Filter && clause.Handler < 0);
                        if (filter >= 0)
                        {
                            Clauses[filter] = Clauses[filter] with { Handler = i };
                        }
                    }
                    else
                    {
                        Clauses.Add(new FlowClause(groups[^1], kind, i, kind == BlockKind.Filter ? -1 : i,
                            CatchesAll: kind == BlockKind.Catch && (node.CatchType is null
                                || EqualityComparer<T>.Default.Equals(node.CatchType, objectType))));
                    }
                }
            }

            Regions[i] = [.. sections];
        }

        Regions[^1] = [.. sections];
        foreach (var group in groups)
        {
            ends[group] = nodes.Count;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var op = node.Instruction?.Op;
            foreach (var targets in node.Targets.Select((target, index) => (target, index))
                .GroupBy(item => item.target, StringComparer.Ordinal))
            {
                if (labels.TryGetValue(targets.Key, out var position))
                {
                    Edges[i].Add(new FlowEdge(position, op == OpCodes.Leave || op == OpCodes.Leave_S,
                        IsExplicit: true, op == OpCodes.Switch ? targets.Select(item => item.index).ToArray() : null));
                }
                else
                {
                    Report(i, "FLOW002", complete ? AnalysisDiagnosticKind.Error : AnalysisDiagnosticKind.Incomplete,
                        complete ? $"branch target '{targets.Key}' is outside an instruction boundary"
                            : $"label '{targets.Key}' is not defined yet");
                }
            }

            if (op is { } instruction && (instruction.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw
                || instruction == OpCodes.Jmp))
            {
                continue;
            }

            var next = i + 1;
            if (next < nodes.Count && nodes[next].Block is { } boundary && boundary != BlockKind.Try)
            {
                var section = Regions[i].LastOrDefault(-1);
                var current = section < 0 ? null : nodes[section].Block;
                if (current is BlockKind.Finally or BlockKind.Fault or BlockKind.Filter)
                {
                    continue;
                }

                if (groupAt.TryGetValue(next, out var group))
                {
                    if (ends[group] < nodes.Count)
                    {
                        Edges[i].Add(new FlowEdge(ends[group], true));
                    }

                    continue;
                }
            }

            Edges[i].Add(new FlowEdge(next));
        }
    }

    /// <summary>
    /// Checks branch destinations and handler-only instructions independently of stack reachability.
    /// </summary>
    public void ValidateRegions()
    {
        ValidatePrefixes();
        for (var index = 0; index < Nodes.Count; index++)
        {
            var op = Nodes[index].Instruction?.Op;
            var source = Regions[index];
            var kinds = source.Select(id => Sections[id].Kind).ToArray();
            if (op == OpCodes.Ret && source.Length > 0)
            {
                Report(index, "FLOW010", AnalysisDiagnosticKind.Error, "ret is not allowed inside a protected region; use leave");
            }

            if (op == OpCodes.Jmp && source.Length > 0)
            {
                Report(index, "FLOW022", AnalysisDiagnosticKind.Error, "jmp is not allowed inside a protected region");
            }

            if (op == OpCodes.Localloc
                && kinds.Any(kind => kind is BlockKind.Catch or BlockKind.Filter or BlockKind.FilterHandler
                    or BlockKind.Finally or BlockKind.Fault))
            {
                Report(index, "FLOW023", AnalysisDiagnosticKind.Error, "localloc is not allowed inside an exception handler");
            }

            if (op == OpCodes.Rethrow && !kinds.Any(kind => kind is BlockKind.Catch or BlockKind.FilterHandler))
            {
                Report(index, "FLOW011", AnalysisDiagnosticKind.Error, "rethrow is only valid inside a catch handler");
            }

            if (op == OpCodes.Endfinally && !kinds.Any(kind => kind is BlockKind.Finally or BlockKind.Fault))
            {
                Report(index, "FLOW012", AnalysisDiagnosticKind.Error, "endfinally is only valid inside finally or fault");
            }

            if (op == OpCodes.Endfilter && (kinds.Length == 0 || kinds[^1] != BlockKind.Filter))
            {
                Report(index, "FLOW013", AnalysisDiagnosticKind.Error, "endfilter is only valid inside a filter");
            }

            if ((op == OpCodes.Leave || op == OpCodes.Leave_S)
                && kinds.Any(kind => kind is BlockKind.Finally or BlockKind.Fault or BlockKind.Filter))
            {
                Report(index, "FLOW014", AnalysisDiagnosticKind.Error, "leave is not allowed inside finally, fault, or filter");
                continue;
            }

            foreach (var edge in Edges[index])
            {
                var target = Regions[edge.Target];
                if (source.SequenceEqual(target))
                {
                    continue;
                }

                if (edge.ClearsStack)
                {
                    var exitsFinalizer = source.Any(id => Sections[id].Kind is BlockKind.Finally or BlockKind.Fault or BlockKind.Filter
                        && !target.Contains(id));
                    var entersProtectedRegion = target.Any(id => !source.Contains(id));
                    if (exitsFinalizer || entersProtectedRegion)
                    {
                        Report(index, "FLOW014", AnalysisDiagnosticKind.Error,
                            "leave cannot transfer control across these handler boundaries");
                    }

                    continue;
                }

                var entersTry = !edge.IsExplicit && target.Length > source.Length && target.Take(source.Length).SequenceEqual(source)
                    && target.Skip(source.Length).All(id => Sections[id].Kind == BlockKind.Try
                        && IsFirstInstruction(edge.Target, Sections[id].Start));
                if (!entersTry)
                {
                    Report(index, "FLOW015", AnalysisDiagnosticKind.Error,
                        "a branch cannot enter or leave this protected region; use leave when exiting a try or catch");
                }
            }
        }
    }

    /// <summary>
    /// Checks stack-sensitive structural rules after all incoming paths have converged.
    /// </summary>
    public void ValidateStacks(IReadOnlyList<FlowState<T>?> before)
    {
        foreach (var section in Sections.Values.Where(section => section.Kind == BlockKind.Try))
        {
            if (before[section.Start]?.Values is { Length: > 0 })
            {
                Report(section.Start, "FLOW016", AnalysisDiagnosticKind.Error, "a try region must begin with an empty stack");
            }
        }

        int InstructionAt(int position)
        {
            while (position < Nodes.Count && Nodes[position].Instruction is null)
            {
                position++;
            }

            return position;
        }

        var forward = new HashSet<int>();
        var backward = new HashSet<int>();
        for (var index = 0; index < Nodes.Count; index++)
        {
            if (Nodes[index].Instruction is null || before[index] is null)
            {
                continue;
            }

            foreach (var edge in Edges[index])
            {
                var target = InstructionAt(edge.Target);
                (index < target ? forward : backward).Add(target);
            }
        }

        foreach (var target in backward)
        {
            if (!forward.Contains(target) && !Seeds.Keys.Any(seed => InstructionAt(seed) == target)
                && before[target]?.Values is { Length: > 0 })
            {
                Report(target, "FLOW017", AnalysisDiagnosticKind.Error,
                    "a backward branch carrying values needs a predecessor at a lower instruction offset (ECMA-335 III.1.7.5)");
            }
        }
    }

    /// <summary>
    /// Finds the finally handlers that a leave executes, ordered from the innermost protected group outward.
    /// </summary>
    public IEnumerable<int> FinalizersForTransfer(int source, int target)
    {
        return UnwindHandlersForTransfer(source, target, exceptional: false);
    }

    /// <summary>
    /// Finds the finally and fault handlers executed while an exception unwinds to a clause.
    /// </summary>
    /// <param name="source">The instruction that threw or completed a filter.</param>
    /// <param name="target">The selected clause entry point.</param>
    /// <returns>The unwind handlers ordered from the innermost protected group outward.</returns>
    public IEnumerable<int> UnwindHandlersForException(int source, int target)
    {
        return UnwindHandlersForTransfer(source, target, exceptional: true);
    }

    private IEnumerable<int> UnwindHandlersForTransfer(int source, int target, bool exceptional)
    {
        var targetGroups = Regions[target].Select(id => Sections[id].Group).ToHashSet();
        var visited = new HashSet<int>();
        for (var index = Regions[source].Length - 1; index >= 0; index--)
        {
            var active = Sections[Regions[source][index]];
            if (targetGroups.Contains(active.Group) || !visited.Add(active.Group)
                || active.Kind is BlockKind.Finally or BlockKind.Fault)
            {
                continue;
            }

            foreach (var (id, section) in Sections)
            {
                if (section.Group == active.Group && (section.Kind == BlockKind.Finally
                    || exceptional && section.Kind == BlockKind.Fault))
                {
                    yield return id;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Finds the handler paired with the filter containing an endfilter instruction.
    /// </summary>
    /// <param name="instruction">The endfilter instruction's position.</param>
    /// <returns>The paired handler's position, or null when the instruction is not inside a filter.</returns>
    public int? FilterHandlerFor(int instruction)
    {
        for (var index = Regions[instruction].Length - 1; index >= 0; index--)
        {
            var filter = Sections[Regions[instruction][index]];
            if (filter.Kind != BlockKind.Filter)
            {
                continue;
            }

            foreach (var section in Sections.Values)
            {
                if (section.Kind == BlockKind.FilterHandler && section.Group == filter.Group && section.Start == filter.End)
                {
                    return section.Start;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Finds the exception-clause kind whose first instruction is the supplied entry point.
    /// </summary>
    public BlockKind? ClauseKindAt(int entry) => Clauses
        .FirstOrDefault(clause => clause.Entry == entry)?.Kind;

    /// <summary>
    /// Finds initial catch candidates through the first filter in exception-search order.
    /// </summary>
    public IEnumerable<int> ExceptionSearchTargets(int instruction)
    {
        foreach (var group in EnclosingTryGroups(instruction))
        {
            foreach (var clause in Clauses.Where(candidate => candidate.Group == group && IsSearchClause(candidate)))
            {
                yield return clause.Entry;
                if (clause.Kind == BlockKind.Filter || IsCatchAll(clause))
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Finds clauses searched after the filter containing this instruction returns zero.
    /// </summary>
    /// <param name="instruction">An instruction inside the rejecting filter.</param>
    /// <returns>Later clause entry points, followed by entries in enclosing protected regions.</returns>
    public IEnumerable<int> FilterContinuationTargets(int instruction)
    {
        var filter = Regions[instruction].Reverse().Select(id => Sections[id])
            .FirstOrDefault(section => section.Kind == BlockKind.Filter);
        if (filter is null)
        {
            yield break;
        }

        var clause = Clauses.FindIndex(candidate => candidate.Group == filter.Group
            && candidate.Kind == BlockKind.Filter && candidate.Entry == filter.Start);
        if (clause < 0)
        {
            yield break;
        }

        var targets = new HashSet<int>();
        for (var index = clause + 1; index < Clauses.Count; index++)
        {
            if (Clauses[index].Group == filter.Group && IsSearchClause(Clauses[index])
                && targets.Add(Clauses[index].Entry))
            {
                yield return Clauses[index].Entry;
                if (Clauses[index].Kind == BlockKind.Filter || IsCatchAll(Clauses[index]))
                {
                    yield break;
                }
            }
        }

        foreach (var group in EnclosingTryGroups(instruction).Where(group => group != filter.Group))
        {
            foreach (var candidate in Clauses.Where(candidate => candidate.Group == group && IsSearchClause(candidate)))
            {
                if (targets.Add(candidate.Entry))
                {
                    yield return candidate.Entry;
                    if (candidate.Kind == BlockKind.Filter || IsCatchAll(candidate))
                    {
                        yield break;
                    }
                }
            }
        }
    }

    private static bool IsSearchClause(FlowClause clause) => clause.Kind is BlockKind.Catch or BlockKind.Filter;

    private static bool IsCatchAll(FlowClause clause) => clause.Kind == BlockKind.Catch && clause.CatchesAll;

    private IEnumerable<int> EnclosingTryGroups(int instruction) => Regions[instruction].Reverse()
        .Select(id => Sections[id]).Where(section => section.Kind == BlockKind.Try)
        .Select(section => section.Group).Distinct();

    /// <summary>
    /// Enumerates the prefixes attached to an instruction, skipping source labels and comments.
    /// </summary>
    public IEnumerable<StackOperandView<T>> Prefixes(int index)
    {
        for (var position = index - 1; position >= 0; position--)
        {
            if (Nodes[position].Block is not null)
            {
                yield break;
            }

            if (Nodes[position].Instruction is not { } instruction)
            {
                continue;
            }

            if (!IsPrefix(instruction))
            {
                yield break;
            }

            yield return instruction;
        }
    }

    private void ValidatePrefixes()
    {
        var prefixes = new List<int>();
        var targets = Nodes.SelectMany(node => node.Targets).ToHashSet(StringComparer.Ordinal);
        var targetedPositions = Nodes.Select((node, index) => (node, index))
            .Where(pair => pair.node.Labels.Any(targets.Contains)).Select(pair => pair.index).ToHashSet();
        for (var index = 0; index < Nodes.Count; index++)
        {
            if (Nodes[index].Instruction is not { } instruction)
            {
                if (Nodes[index].Block is not null && prefixes.Count != 0)
                {
                    Report(index, "FLOW019", AnalysisDiagnosticKind.Error,
                        "a protected-region boundary cannot separate a prefix from its instruction");
                    prefixes.Clear();
                }

                continue;
            }

            if (prefixes.Count > 0)
            {
                var first = prefixes[0];
                if (Enumerable.Range(first + 1, index - first).Any(targetedPositions.Contains))
                {
                    Report(index, "FLOW018", AnalysisDiagnosticKind.Error, "a branch must target the first prefix of an instruction");
                }
            }

            if (IsPrefix(instruction))
            {
                if (instruction.Op == OpCodes.Unaligned && instruction.ByteOperand is { } alignment
                    && alignment is not (1 or 2 or 4))
                {
                    Report(index, "FLOW019", AnalysisDiagnosticKind.Error,
                        $"unaligned. alignment must be 1, 2, or 4 but found {alignment}");
                }

                prefixes.Add(index);
                continue;
            }

            foreach (var prefix in prefixes)
            {
                var prefixInstruction = Nodes[prefix].Instruction!;
                var name = PrefixName(prefixInstruction);
                var op = instruction.Op.Name ?? "";
                var memory = op.StartsWith("ldind", StringComparison.Ordinal) || op.StartsWith("stind", StringComparison.Ordinal)
                    || op is "ldfld" or "stfld" or "ldobj" or "stobj" or "initblk" or "cpblk";
                var allowed = name switch
                {
                    "readonly." => op == "ldelema",
                    "constrained." => op is "callvirt" or "call" or "ldftn",
                    "tail." => op is "call" or "callvirt" or "calli",
                    "volatile." => memory || op is "ldsfld" or "stsfld",
                    "unaligned." => memory,
                    "no." => NoPrefixAllows(prefixInstruction.ByteOperand, op),
                    _ => true,
                };
                if (!allowed)
                {
                    Report(prefix, "FLOW019", AnalysisDiagnosticKind.Error, $"{name} cannot prefix {op}");
                }

                if (name == "tail." && allowed)
                {
                    var next = index + 1;
                    while (next < Nodes.Count && Nodes[next].Instruction is null)
                    {
                        next++;
                    }

                    if (next == Nodes.Count)
                    {
                        Report(prefix, "FLOW021", AnalysisDiagnosticKind.Incomplete, "a tail call must be followed by ret");
                    }
                    else if (Nodes[next].Instruction!.Op != OpCodes.Ret || Regions[index].Length > 0)
                    {
                        Report(prefix, "FLOW021", AnalysisDiagnosticKind.Error,
                            "a tail call must be followed by ret outside protected regions");
                    }
                }
            }

            prefixes.Clear();
        }

        foreach (var prefix in prefixes)
        {
            Report(prefix, "FLOW020", AnalysisDiagnosticKind.Incomplete, "the prefix is waiting for its instruction");
        }
    }

    private static bool IsPrefix(StackOperandView<T> instruction) => instruction.DecodedPrefixName is not null
        || instruction.Op.OpCodeType == OpCodeType.Prefix;

    private static string PrefixName(StackOperandView<T> instruction) => instruction.DecodedPrefixName
        ?? instruction.Op.Name ?? "";

    private static bool NoPrefixAllows(byte? mask, string op)
    {
        if (mask is not { } checks || checks == 0 || (checks & ~0x07) != 0)
        {
            return false;
        }

        var array = op.StartsWith("ldelem", StringComparison.Ordinal) || op.StartsWith("stelem", StringComparison.Ordinal)
            || op == "ldelema";
        var typeCheck = op is "castclass" or "unbox" or "ldelema" or "stelem" or "stelem.ref";
        var nullCheck = array || op is "ldfld" or "stfld" or "callvirt" or "ldvirtftn";
        return ((checks & 0x01) == 0 || typeCheck) && ((checks & 0x02) == 0 || array)
            && ((checks & 0x04) == 0 || nullCheck);
    }

    private bool IsFirstInstruction(int target, int start)
    {
        for (var index = start; index < target; index++)
        {
            if (Nodes[index].Instruction is not null)
            {
                return false;
            }
        }

        return true;
    }

    private void Report(int index, string code, AnalysisDiagnosticKind kind, string message) =>
        Diagnostics.Add(new AnalysisDiagnostic(code, kind, message, Nodes[index].Location, []));
}
