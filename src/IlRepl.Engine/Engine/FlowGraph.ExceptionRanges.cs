using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Maps ordered exception ranges to control-flow boundaries.
/// </summary>
internal sealed partial class FlowGraph<T> where T : class
{
    private void AddExceptionRanges(T objectType, bool complete, bool hasThis)
    {
        var ranges = Nodes.Select((node, index) => (node, index))
            .Where(item => item.node.ExceptionRegion is not null).ToArray();
        if (ranges.Length == 0)
        {
            return;
        }

        var labels = Nodes.SelectMany((node, index) => node.Labels.Select(label => (label, index)))
            .GroupBy(item => item.label, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        var nextSection = Sections.Count == 0 ? 0 : Sections.Keys.Max() + 1;
        var groups = new Dictionary<(int Start, int End, string? PendingStart, string? PendingEnd), int>();
        var pendingEnds = new HashSet<int>(Regions[Nodes.Count]);
        var constructorState = Seeds[0].ConstructorState;

        int Boundary(string label, int location, bool end)
        {
            if (labels.TryGetValue(label, out var position))
            {
                // Labels and directives emit no bytes. Adjacent labels therefore share the
                // same exception boundary even when they occupy separate source entries.
                while (position > 0 && Nodes[position - 1].Instruction is null
                    && Nodes[position - 1].Block is null && !Nodes[position - 1].EffectUnknown)
                {
                    position--;
                }

                return position;
            }

            Report(location, "FLOW025", complete ? AnalysisDiagnosticKind.Error : AnalysisDiagnosticKind.Incomplete,
                $"exception boundary '{label}' is not defined yet");
            return end ? Nodes.Count : -1;
        }

        int Section(BlockKind kind, int start, int end, int group, bool pendingEnd)
        {
            var id = nextSection++;
            Sections[id] = new FlowRegion(kind, start, end, group);
            if (pendingEnd)
            {
                pendingEnds.Add(id);
            }

            return id;
        }

        void Seed(int position, T? caught, int handler)
        {
            var state = caught is null
                ? hasThis ? FlowState<T>.ThisEntry with { ConstructorState = constructorState } : FlowState<T>.Empty
                : new FlowState<T>([new FlowValue<T>(caught, [position])], ThisArgumentIsOriginal: hasThis,
                    ConstructorState: constructorState);
            Seeds[position] = new ExceptionalFlowState<T>(state, null, handler);
        }

        foreach (var (node, index) in ranges)
        {
            var region = node.ExceptionRegion!;
            var start = Boundary(region.TryStart, index, false);
            var end = Boundary(region.TryEnd, index, true);
            var handlerStart = Boundary(region.HandlerStart, index, false);
            var handlerEnd = Boundary(region.HandlerEnd, index, true);
            var filter = region.FilterStart is null ? -1 : Boundary(region.FilterStart, index, false);
            if (start >= 0 && labels.ContainsKey(region.TryEnd) && start >= end
                || handlerStart >= 0 && labels.ContainsKey(region.HandlerEnd) && handlerStart >= handlerEnd
                || filter >= 0 && handlerStart >= 0 && filter >= handlerStart)
            {
                Report(index, "FLOW026", AnalysisDiagnosticKind.Error,
                    $"exception ranges must be nonempty and ordered: try {region.TryStart} to {region.TryEnd}, "
                    + $"handler {region.HandlerStart} to {region.HandlerEnd}"
                    + (region.FilterStart is null ? "" : $", filter {region.FilterStart}"));
                continue;
            }

            var key = (start, end, start < 0 ? region.TryStart : null,
                labels.ContainsKey(region.TryEnd) ? null : region.TryEnd);
            if (!groups.TryGetValue(key, out var group))
            {
                group = nextSection;
                groups.Add(key, group);
                if (start >= 0)
                {
                    Section(BlockKind.Try, start, end, group, !labels.ContainsKey(region.TryEnd));
                }
                else
                {
                    nextSection++;
                }
            }

            var kind = region.Kind switch
            {
                IlClauseKind.Catch => BlockKind.Catch,
                IlClauseKind.Filter => BlockKind.FilterHandler,
                IlClauseKind.Finally => BlockKind.Finally,
                _ => BlockKind.Fault,
            };

            if (handlerStart >= 0)
            {
                var handler = Section(kind, handlerStart, handlerEnd, group, !labels.ContainsKey(region.HandlerEnd));
                Seed(handlerStart, region.Kind is IlClauseKind.Catch or IlClauseKind.Filter
                    ? region.CatchType ?? objectType : null, handler);
            }

            if (filter >= 0)
            {
                var handler = Section(BlockKind.Filter, filter, handlerStart < 0 ? Nodes.Count : handlerStart,
                    group, handlerStart < 0);
                Seed(filter, objectType, handler);
            }

            if (handlerStart >= 0 || filter >= 0)
            {
                Clauses.Add(new FlowClause(group, region.Kind == IlClauseKind.Filter ? BlockKind.Filter : kind,
                    filter >= 0 ? filter : handlerStart, handlerStart < 0 ? Nodes.Count : handlerStart,
                    CatchesAll: region.Kind == IlClauseKind.Catch && EqualityComparer<T>.Default.Equals(region.CatchType, objectType)));
            }
        }

        for (var index = 0; index <= Nodes.Count; index++)
        {
            Regions[index] = Sections.Where(pair => index >= pair.Value.Start
                && (index < pair.Value.End || index == Nodes.Count && pendingEnds.Contains(pair.Key)))
                .OrderBy(pair => pair.Value.Start).ThenByDescending(pair => pair.Value.End)
                .Select(pair => pair.Key).ToArray();
        }
    }
}
