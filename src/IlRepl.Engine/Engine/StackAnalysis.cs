using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Fills the stack column of a disassembly: a worklist over basic blocks, each instruction's
/// effect from the same <see cref="StackSimulator"/> that <c>.show</c> uses, states merged at
/// joins the way the CLI merges them (ECMA-335 III.1.8.1.3), handlers seeded from the clauses.
/// Three answers are kept apart: a known stack, an unknown one (<c>?</c>), and a line no path
/// reaches (<c>unreachable</c>). Unknown is absorbing: only a handler seed or the empty stack a
/// <c>leave</c> carries brings a known state back.
/// </summary>
public static class StackAnalysis
{
    /// <summary>
    /// The column text for an unknown stack.
    /// </summary>
    public const string Unknown = "?";

    /// <summary>
    /// The column text for a line no path reaches.
    /// </summary>
    public const string Unreachable = "unreachable";

    /// <summary>
    /// Computes the column: one text per entry, null for labels and block lines.
    /// </summary>
    /// <param name="method">The disassembled method.</param>
    /// <returns>The column.</returns>
    public static IReadOnlyList<string?> Run(DisassembledMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var column = new string?[method.Entries.Count];
        var positions = new List<int>();
        for (var i = 0; i < method.Entries.Count; i++)
        {
            if (method.Entries[i].Kind is DisassembledEntryKind.Instruction or DisassembledEntryKind.Raw)
            {
                positions.Add(i);
            }
        }

        if (positions.Count == 0)
        {
            return column;
        }

        var offsetToPosition = new Dictionary<int, int>();
        for (var p = 0; p < positions.Count; p++)
        {
            offsetToPosition[method.Entries[positions[p]].Offset] = p;
        }

        // Leaders: the entry, every target, the line after a branch or an exit, and every clause boundary.
        var leaderPositions = new HashSet<int> { 0 };
        void Lead(int offset)
        {
            if (offsetToPosition.TryGetValue(offset, out var lp))
            {
                leaderPositions.Add(lp);
            }
        }

        for (var p = 0; p < positions.Count; p++)
        {
            var entry = method.Entries[positions[p]];
            var raw = entry.Raw;
            if (raw is null)
            {
                continue;
            }

            if (raw.BranchTarget is int target)
            {
                Lead(target);
                leaderPositions.Add(p + 1);
            }

            if (raw.Operand.SwitchTargets.Length > 0)
            {
                foreach (var t in raw.Operand.SwitchTargets)
                {
                    Lead(t);
                }

                leaderPositions.Add(p + 1);
            }

            if (entry.Instruction?.EndsFlow == true || IsExit(entry))
            {
                leaderPositions.Add(p + 1);
            }
        }

        foreach (var clause in method.Clauses)
        {
            Lead(clause.TryStart);
            Lead(clause.HandlerStart);
            if (clause.FilterStart is int filter)
            {
                Lead(filter);
            }
        }

        var starts = leaderPositions.Where(l => l >= 0 && l < positions.Count).OrderBy(l => l).ToList();
        var blockOf = new int[positions.Count];
        var blockStart = new List<int>();
        var blockEnd = new List<int>();
        for (var b = 0; b < starts.Count; b++)
        {
            var end = b + 1 < starts.Count ? starts[b + 1] : positions.Count;
            blockStart.Add(starts[b]);
            blockEnd.Add(end);
            for (var p = starts[b]; p < end; p++)
            {
                blockOf[p] = b;
            }
        }

        var entryState = new StackSimulator?[blockStart.Count];
        var entryUnknown = new bool[blockStart.Count];
        var visited = new bool[blockStart.Count];
        var seeded = new bool[blockStart.Count];
        var queue = new Queue<int>();

        void Seed(int offset, StackSimulator state)
        {
            if (!offsetToPosition.TryGetValue(offset, out var p))
            {
                return;
            }

            var b = blockOf[p];
            entryState[b] = state;
            entryUnknown[b] = false;
            visited[b] = true;
            seeded[b] = true;
            queue.Enqueue(b);
        }

        Seed(method.Entries[positions[0]].Offset, new StackSimulator());
        foreach (var clause in method.Clauses)
        {
            switch (clause.Kind)
            {
                case IlClauseKind.Catch:
                    Seed(clause.HandlerStart, StackSimulator.WithEntries(clause.CatchType));
                    break;
                case IlClauseKind.Filter:
                    Seed(clause.FilterStart ?? clause.HandlerStart, StackSimulator.WithEntries(typeof(object)));
                    Seed(clause.HandlerStart, StackSimulator.WithEntries(typeof(object)));
                    break;
                default:
                    Seed(clause.HandlerStart, new StackSimulator());
                    break;
            }
        }

        void Propagate(int offset, StackSimulator? state)
        {
            if (!offsetToPosition.TryGetValue(offset, out var p))
            {
                return;
            }

            var b = blockOf[p];
            if (seeded[b])
            {
                return;
            }

            if (state is null)
            {
                if (!visited[b] || !entryUnknown[b])
                {
                    visited[b] = true;
                    entryUnknown[b] = true;
                    entryState[b] = null;
                    queue.Enqueue(b);
                }

                return;
            }

            if (!visited[b])
            {
                visited[b] = true;
                entryState[b] = state.Clone();
                queue.Enqueue(b);
                return;
            }

            if (entryUnknown[b])
            {
                return;
            }

            if (entryState[b]!.Count != state.Count)
            {
                entryUnknown[b] = true;
                entryState[b] = null;
                queue.Enqueue(b);
                return;
            }

            if (entryState[b]!.Merge(state, method.Context.Types))
            {
                queue.Enqueue(b);
            }
        }

        var guard = 0;
        while (queue.Count > 0 && guard++ < 100_000)
        {
            var b = queue.Dequeue();
            var state = entryUnknown[b] ? null : entryState[b]!.Clone();
            for (var p = blockStart[b]; p < blockEnd[b]; p++)
            {
                var index = positions[p];
                var entry = method.Entries[index];
                if (state is not null && !entry.EffectUnknown && entry.Instruction is { } instruction)
                {
                    try
                    {
                        state.Apply(instruction, method.Context);
                    }
                    catch (ReplException)
                    {
                        state = null;
                    }
                }
                else if (entry.Kind == DisassembledEntryKind.Raw && !entry.EffectUnknown)
                {
                    // A no. prefix: no effect.
                }
                else
                {
                    state = null;
                }

                column[index] = state?.Render() ?? Unknown;
            }

            var last = method.Entries[positions[blockEnd[b] - 1]];
            var raw = last.Raw;
            var op = last.Instruction?.Op;
            if (raw?.BranchTarget is int target)
            {
                // A leave empties the stack whatever was on it, so its target starts from a known
                // state even when the block before it was unknown.
                Propagate(target, op == OpCodes.Leave || op == OpCodes.Leave_S ? new StackSimulator() : state);
            }

            if (raw is not null)
            {
                foreach (var t in raw.Operand.SwitchTargets)
                {
                    Propagate(t, state);
                }
            }

            var endsFlow = last.Instruction?.EndsFlow == true || IsExit(last) || op == OpCodes.Leave || op == OpCodes.Leave_S;
            if (!endsFlow && blockEnd[b] < positions.Count)
            {
                Propagate(method.Entries[positions[blockEnd[b]]].Offset, state);
            }
        }

        for (var p = 0; p < positions.Count; p++)
        {
            if (!visited[blockOf[p]])
            {
                column[positions[p]] = Unreachable;
            }
        }

        return column;
    }

    private static bool IsExit(DisassembledEntry entry)
    {
        var op = entry.Instruction?.Op;
        return op == OpCodes.Endfinally || op == OpCodes.Endfilter || op == OpCodes.Rethrow || op == OpCodes.Throw;
    }
}
