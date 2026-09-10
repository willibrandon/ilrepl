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
    /// Findings established while connecting the body.
    /// </summary>
    public List<AnalysisDiagnostic> Diagnostics { get; } = [];

    /// <summary>
    /// Builds a graph using the supplied exception-object type.
    /// </summary>
    public FlowGraph(IReadOnlyList<FlowNode<T>> nodes, T objectType, bool complete = false)
    {
        Nodes = nodes;
        Edges = Enumerable.Range(0, nodes.Count + 1).Select(_ => new List<FlowEdge>()).ToArray();
        Regions = new int[nodes.Count + 1][];
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        var sections = new List<int>();
        var groups = new List<int>();
        var ends = new Dictionary<int, int>();
        var groupAt = new Dictionary<int, int>();
        Seeds[0] = FlowState<T>.Empty;
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
                    Seeds[i] = kind is BlockKind.Catch or BlockKind.Filter or BlockKind.FilterHandler
                        ? new FlowState<T>([new FlowValue<T>(node.CatchType ?? objectType, [i])]) : FlowState<T>.Empty;
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
            foreach (var target in node.Targets.Distinct(StringComparer.Ordinal))
            {
                if (labels.TryGetValue(target, out var position))
                {
                    Edges[i].Add(new FlowEdge(position, op == OpCodes.Leave || op == OpCodes.Leave_S, IsExplicit: true));
                }
                else
                {
                    Report(i, "FLOW002", complete ? AnalysisDiagnosticKind.Error : AnalysisDiagnosticKind.Incomplete,
                        complete ? $"branch target '{target}' is outside an instruction boundary" : $"label '{target}' is not defined yet");
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

                var entersTry = !edge.IsExplicit && target.Length == source.Length + 1 && target.Take(source.Length).SequenceEqual(source)
                    && Sections[target[^1]].Kind == BlockKind.Try && IsFirstInstruction(edge.Target, Sections[target[^1]].Start);
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
            if (Nodes[index].Instruction is null)
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

            if (instruction.Op.OpCodeType != OpCodeType.Prefix)
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

            if (instruction.Op.OpCodeType == OpCodeType.Prefix)
            {
                prefixes.Add(index);
                continue;
            }

            foreach (var prefix in prefixes)
            {
                var name = Nodes[prefix].Instruction!.Op.Name;
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
