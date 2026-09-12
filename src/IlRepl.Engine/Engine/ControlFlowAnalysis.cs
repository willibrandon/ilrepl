using System.Reflection.Emit;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Computes stack states and producer locations to a fixed point over runtime or symbolic bodies.
/// </summary>
/// <typeparam name="T">The type representation.</typeparam>
internal sealed class ControlFlowAnalysis<T>(FlowTypeRules<T> types) where T : class
{
    private const int MaxFilterPaths = 64;
    private const int MaxCorrelatedAlternatives = 256;
    private static readonly FilterPathValue UnknownReceiverValue = new(
        null, null, false, HasNonSourceAlternative: true);
    private static readonly FilterPathState IdentityReceiverTransformation = new([], null, null, true);
    private static readonly FilterPathState[] IdentityReceiverTransformations = [IdentityReceiverTransformation];
    private readonly FlowTypeRules<T> _types = types;
    private readonly StackTransfer<T> _transfer = new(types.Algebra);

    /// <summary>
    /// Analyzes every established path without executing or materializing user definitions.
    /// </summary>
    public FlowResult<T> Run(FlowGraph<T> graph, T? returnType, bool cell, CancellationToken cancellationToken = default)
    {
        return Steps(graph, returnType, cell, cancellationToken).Last()!;
    }

    /// <summary>
    /// Cooperatively analyzes large bodies so browser input and cancellation remain responsive.
    /// </summary>
    public async ValueTask<FlowResult<T>> RunAsync(FlowGraph<T> graph, T? returnType, bool cell,
        CancellationToken cancellationToken = default)
    {
        foreach (var result in Steps(graph, returnType, cell, cancellationToken))
        {
            if (result is not null)
            {
                return result;
            }

            await Task.Yield();
        }

        throw new InvalidOperationException("Analysis did not produce a result.");
    }

    private IEnumerable<FlowResult<T>?> Steps(FlowGraph<T> graph, T? returnType, bool cell, CancellationToken cancellationToken)
    {
        Dictionary<int, (bool Completes, FilterPathState[] Transformations)>? unwindEffects = null;
        if (graph.Sections.Values.Any(section => section.Kind is BlockKind.Finally or BlockKind.Fault))
        {
            unwindEffects = [];
            foreach (var (id, section) in graph.Sections
                .Where(pair => pair.Value.Kind is BlockKind.Finally or BlockKind.Fault)
                .OrderBy(pair => pair.Value.End - pair.Value.Start))
            {
                var seeds = new Dictionary<int, FlowState<T>>
                {
                    [section.Start] = FlowState<T>.ThisEntry with
                    {
                        FilterPaths = [new FilterPathState([], null, null, true)],
                    },
                };
                var result = (FlowResult<T>?)null;
                foreach (var step in AnalyzeSteps(graph, returnType, cell, cancellationToken, seeds,
                    section.Start, section.End, unwindEffects, validateGraph: false))
                {
                    if (step is null)
                    {
                        yield return null;
                    }
                    else
                    {
                        result = step;
                    }
                }

                unwindEffects[id] = FinalizerEffect(graph, id, section, result!);
            }
        }

        foreach (var result in AnalyzeSteps(graph, returnType, cell, cancellationToken,
            finalizerEffects: unwindEffects, validateGraph: true))
        {
            yield return result;
        }
    }

    private IEnumerable<FlowResult<T>?> AnalyzeSteps(FlowGraph<T> graph, T? returnType, bool cell,
        CancellationToken cancellationToken, IReadOnlyDictionary<int, FlowState<T>>? seeds = null,
        int start = 0, int? end = null,
        Dictionary<int, (bool Completes, FilterPathState[] Transformations)>? finalizerEffects = null,
        bool validateGraph = true)
    {
        var nodes = graph.Nodes;
        var limit = end ?? nodes.Count + 1;
        var count = limit - start;
        if (validateGraph)
        {
            graph.ValidateRegions();
        }
        var before = new FlowState<T>?[count];
        var after = new FlowState<T>?[count];
        var incomingStates = new FlowState<T>?[count];
        var incomingPredecessors = new int[count];
        var additionalIncoming = new Dictionary<int, FlowState<T>>?[count];
        var diagnostics = new Dictionary<(int, string), AnalysisDiagnostic>();
        var queue = new Queue<int>();
        var queued = new bool[count];
        var unwindEntries = (Dictionary<(int Source, int Handler), FlowState<T>>?)null;
        var maxStack = 0;
        var tracksFilterPaths = graph.Sections.Values.Any(section =>
            section.Kind is BlockKind.Filter or BlockKind.Finally or BlockKind.Fault);

        void Enqueue(int position)
        {
            var slot = position - start;
            if (!queued[slot])
            {
                queued[slot] = true;
                queue.Enqueue(position);
            }
        }

        void Report(int position, string code, string message, AnalysisDiagnosticKind kind = AnalysisDiagnosticKind.Error,
            IReadOnlyList<AnalysisRelatedLocation>? related = null)
        {
            var location = position < nodes.Count ? nodes[position].Location
                : nodes.Count > 0 ? nodes[^1].Location : new AnalysisLocation("cell", -1, 0, 0);
            diagnostics[(position, code)] = new AnalysisDiagnostic(code, kind, message, location, related ?? []);
        }

        void ContinueFilterSearch(int source, FilterPathState[]? paths, bool fallbackThis,
            IReadOnlyList<int>? fallbackUnwindHandlers)
        {
            foreach (var target in graph.FilterContinuationTargets(source))
            {
                var outgoing = WithExceptional(new FlowState<T>([], ThisArgumentIsOriginal: fallbackThis,
                    FilterPaths: paths), fallbackUnwindHandlers, null);
                outgoing = QueueUnwindHandlers(outgoing, graph.UnwindHandlersForException(source, target),
                    finalizerEffects);
                if (graph.ClauseKindAt(target) != BlockKind.Filter)
                {
                    outgoing = ApplyUnwindEffects(outgoing, finalizerEffects,
                        (handler, incoming) => EnterUnwindHandler(source, handler, incoming));
                    if (outgoing is null)
                    {
                        continue;
                    }
                }
                var entry = graph.Seeds[target];
                var targetState = entry with
                {
                    ThisArgumentIsOriginal = outgoing.ThisArgumentIsOriginal,
                    FilterPaths = EnterExceptionRegion(outgoing.FilterPaths, entry),
                };
                Propagate(target, source, WithExceptional(targetState,
                    outgoing.PendingUnwindHandlers, null));
            }
        }

        void EnterUnwindHandler(int source, int handler, FlowState<T> incoming)
        {
            if (!graph.Sections.TryGetValue(handler, out var section))
            {
                return;
            }

            var entry = graph.Seeds.TryGetValue(section.Start, out var seeded) ? seeded : FlowState<T>.Empty;
            var paths = incoming.FilterPaths is null ? null : ExpandFilterPaths(incoming.FilterPaths).Select(path => path with
            {
                Values = [],
                PendingUnwindEffect = null,
            }).ToArray();
            var state = WithExceptional(entry with
            {
                ThisArgumentIsOriginal = incoming.ThisArgumentIsOriginal,
                FilterPaths = paths,
            }, null, incoming.SyntheticHandler);
            var key = (source, handler);
            unwindEntries ??= [];
            if (unwindEntries.TryGetValue(key, out var current))
            {
                _ = TryMerge(current, state, out state);
            }
            unwindEntries[key] = state;
            Propagate(section.Start, source, state);
        }

        void Propagate(int target, int predecessor, FlowState<T> state)
        {
            if (target < start || target >= limit)
            {
                return;
            }

            var slot = target - start;
            if (incomingStates[slot] is null)
            {
                incomingStates[slot] = state;
                incomingPredecessors[slot] = predecessor;
                Enqueue(target);
                return;
            }

            if (incomingPredecessors[slot] == predecessor)
            {
                if (!Equal(incomingStates[slot]!, state))
                {
                    incomingStates[slot] = state;
                    Enqueue(target);
                }

                return;
            }

            var others = additionalIncoming[slot] ??= [];
            if (!others.TryGetValue(predecessor, out var old) || !Equal(old, state))
            {
                others[predecessor] = state;
                Enqueue(target);
            }
        }

        var structuralProblems = new string?[Math.Min(limit, nodes.Count) - start];
        for (var index = start; index < Math.Min(limit, nodes.Count); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > start && (index - start) % 64 == 0)
            {
                yield return null;
            }

            if (nodes[index].Instruction is not { } instruction || ValidateStructure(instruction, graph, index) is not { } problem)
            {
                continue;
            }

            structuralProblems[index - start] = problem;
            Report(index, "FLOW005", problem);
        }

        KeyValuePair<int, FlowState<T>>[]? deferredEntries = null;
        if (seeds is not null)
        {
            foreach (var (position, seed) in seeds)
            {
                Propagate(position, -1, tracksFilterPaths ? StartFilterPaths(seed) : seed);
            }
        }
        else
        {
            foreach (var (position, seed) in graph.Seeds)
            {
                if (position == 0)
                {
                    Propagate(position, -1, tracksFilterPaths ? StartFilterPaths(seed) : seed);
                }
            }

            if (graph.Seeds.Count > 1)
            {
                deferredEntries = [.. graph.Seeds.Where(entry => entry.Key != 0)];
            }
        }

        var iterations = 0;
    AnalyzeQueue:
        while (queue.TryDequeue(out var index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations % 64 == 0)
            {
                yield return null;
            }
            var slot = index - start;
            queued[slot] = false;
            var state = incomingStates[slot];
            var first = incomingPredecessors[slot];
            if (additionalIncoming[slot] is { } others)
            {
                var predecessors = new KeyValuePair<int, FlowState<T>>[others.Count + 1];
                predecessors[0] = new KeyValuePair<int, FlowState<T>>(first, state!);
                var position = 1;
                foreach (var pair in others)
                {
                    predecessors[position++] = pair;
                }

                Array.Sort(predecessors, static (left, right) => left.Key.CompareTo(right.Key));
                state = null;
                first = -1;

                foreach (var (predecessor, value) in predecessors)
                {
                    if (state is null)
                    {
                        state = value;
                        first = predecessor;
                    }
                    else if (!TryMerge(state, value, out var merged))
                    {
                        var label = index < nodes.Count && nodes[index].Labels.Count > 0 ? nodes[index].Labels[0] : null;
                        var where = label ?? "this instruction";
                        var left = _types.Render(state);
                        var right = _types.Render(value);
                        var related = Related(nodes, first, state).Concat(Related(nodes, predecessor, value)).Distinct().ToArray();
                        Report(index, "FLOW003", $"{where} receives incompatible stacks: {left} from {Path(nodes, first)}"
                            + $" and {right} from {Path(nodes, predecessor)}", related: related);
                        state = new FlowState<T>(null, Invalid: true);
                    }
                    else
                    {
                        state = merged;
                    }
                }
            }

            before[slot] = state;
            if (state is null)
            {
                continue;
            }

            maxStack = Math.Max(maxStack, state.Values?.Length ?? 0);
            if (index == nodes.Count)
            {
                after[slot] = state;
                continue;
            }

            var node = nodes[index];
            var filterPathsBeforeInstruction = state.FilterPaths;
            var transferredPops = 0;
            var transferredPushes = 0;
            var transferredValues = (FlowValue<T>[]?)null;
            var filterPathsAtEnd = !state.Invalid && node.Instruction?.Op == OpCodes.Endfilter ? state.FilterPaths : null;
            if (node.EffectUnknown)
            {
                Report(index, "FLOW004", "the instruction's stack effect is unknown", AnalysisDiagnosticKind.Unknown);
                state = state.Invalid ? state : state with
                {
                    Values = null,
                    HasUnknownPath = true,
                    FilterPaths = UnknownFilterPathStacks(state.FilterPaths),
                };
            }
            else if (node.Instruction is { } view && !state.Invalid && state.Values is { } values)
            {
                var problem = structuralProblems[slot] ?? ValidateStack(view, values, graph, index, returnType, cell,
                    state.FilterPaths is not { Length: 0 });
                if (problem is not null)
                {
                    if (structuralProblems[slot] is null)
                    {
                        Report(index, "FLOW005", problem, related: Related(nodes, index, state));
                    }

                    state = new FlowState<T>(null, Invalid: true);
                }
                else
                {
                    var pops = view.Op == OpCodes.Ret ? cell ? values.Length : returnType is null ? 0 : 1
                        : StackTransfer<T>.PopCount(view);
                    if (pops > values.Length)
                    {
                        var suffix = pops == 1 ? "" : "s";
                        Report(index, "FLOW006", $"stack underflow: '{view.Op.Name}' pops {pops} value{suffix}"
                            + $" but the stack has {values.Length}: {_types.Render(state)}", related: Related(nodes, index, state));
                        state = new FlowState<T>(null, Invalid: true);
                    }
                    else
                    {
                        var popped = values.Skip(values.Length - pops).ToArray();
                        var output = values.Take(values.Length - pops).ToList();
                        var pushed = _transfer.PushTypes(view, popped.Select(value => value.Type).ToArray());
                        transferredPops = pops;
                        transferredPushes = pushed.Count;
                        transferredValues = popped;
                        var filterPaths = TransferFilterPaths(state.FilterPaths, view, pops, pushed.Count, popped);
                        var loadsThis = view.ReadsThisArgument && state.ThisArgumentIsOriginal;
                        for (var pushedIndex = 0; pushedIndex < pushed.Count; pushedIndex++)
                        {
                            var outputIndex = values.Length - pops + pushedIndex;
                            var pathLoadsThis = filterPaths is { Length: > 0 }
                                && filterPaths.All(path => !path.StackUnknown && path.Values.Length > outputIndex
                                    && path.Values[outputIndex].IsThis);
                            var valueIsThis = filterPaths is null ? loadsThis : pathLoadsThis;
                            output.Add(view.Op == OpCodes.Dup ? popped[0] with { IsThis = valueIsThis }
                                : new FlowValue<T>(pushed[pushedIndex], [index], valueIsThis,
                                    graph.Prefixes(index).Any(prefix => prefix.Op == OpCodes.Readonly)
                                        || view.Op == OpCodes.Unbox
                                        || view.Op == OpCodes.Ldflda && popped.Any(value => value.IsReadOnly)));
                        }

                        if (StackTransfer<T>.EndsPath(view.Op))
                        {
                            output.Clear();
                        }

                        var thisArgumentIsOriginal = view.WritesThisArgument ? popped[^1].IsThis
                            : view.ReadsThisArgument && view.Op.Name is "ldarga" or "ldarga.s" ? false
                            : state.ThisArgumentIsOriginal;
                        if (filterPaths is { Length: > 0 })
                        {
                            thisArgumentIsOriginal = filterPaths.All(path => path.ThisArgumentIsOriginal);
                        }
                        state = WithExceptional(new FlowState<T>([.. output], state.HasUnknownPath,
                            ThisArgumentIsOriginal: thisArgumentIsOriginal,
                            FilterPaths: filterPaths),
                            state.PendingUnwindHandlers, state.SyntheticHandler);
                    }
                }

                var tailCallPassesManagedPointer = TailCallPassesManagedPointer(graph, index, view, values);
                var integerAssignedToPointer = IntegerAssignedToPointer(view, values, returnType);
                var nativePointerArrayInstruction = UsesNativePointerArrayInstruction(view, values);
                if (tailCallPassesManagedPointer || integerAssignedToPointer || nativePointerArrayInstruction
                    || view.DecodedPrefixName == "no."
                    || view.Op.Name is "localloc" or "cpblk" or "initblk" or "calli" or "jmp"
                    || view.Op == OpCodes.Mkrefany && values.LastOrDefault()?.Type is { } typedReference
                        && _types.Category(typedReference) == StackCategory.NativeInt
                    || LoadsPointerSlot(view)
                    || LoadsPointerSlotIndirectly(view, values)
                    || TransformsDataPointer(view, values)
                    || UsesNativeAddress(view, values)
                    || UsesGenericReferenceAddress(view, values)
                    || view.Op.Name is "add" or "sub" or "add.ovf.un" or "sub.ovf.un"
                        && values.TakeLast(2).Any(value => value.Type is { } type && _types.Algebra.IsByRef(type))
                    || view.Op.Name?.StartsWith("conv.", StringComparison.Ordinal) == true
                        && values.LastOrDefault()?.Type is { } converted
                        && _types.Category(converted) is StackCategory.ByRef or StackCategory.ObjectReference
                    || FlowNumericRules.Comparison(view.Op.Name!) && values.Length >= 2
                        && values.TakeLast(2).Any(value => value.Type is { } type && _types.Category(type) == StackCategory.ByRef)
                        && values.TakeLast(2).Any(value => value.Type is { } type && _types.Category(type) == StackCategory.NativeInt)
                    || UsesReadOnlyAsWritable(view, values)
                    || view.Op.Name is "call" or "callvirt" or "newobj" && values.Length >= view.ArgumentPops
                        && view.ParameterTypes.Select((parameter, argument) =>
                        (parameter, actual: values[values.Length - view.ArgumentPops + (view.IsInstance ? 1 : 0) + argument].Type))
                        .Any(pair => pair.actual is { } actual && _types.Algebra.IsGenericParameter(actual)
                            && !_types.Algebra.Same(actual, pair.parameter))
                    || (view.Op.Name is "ldftn" or "ldvirtftn") && view.MethodIsConstructor == true
                        && view.MethodIsStatic == false)
                {
                    var operation = tailCallPassesManagedPointer ? "tail." : view.DecodedPrefixName ?? view.Op.Name;
                    Report(index, "FLOW007", $"{operation} uses an operation outside verifiable IL",
                        AnalysisDiagnosticKind.Unverifiable);
                }
            }

            if (!node.EffectUnknown && node.Instruction is { } unknownStackView && !state.Invalid
                && state.Values is null && state.FilterPaths is not null)
            {
                state = state with
                {
                    FilterPaths = TransferFilterPaths(state.FilterPaths, unknownStackView, 0, 0, []),
                };
            }

            after[slot] = state;
            maxStack = Math.Max(maxStack, state.Values?.Length ?? 0);
            // ECMA-335 III.3.34 sends zero onward and one to the paired handler. An unknown result can take either path.
            var pathsAtEnd = filterPathsAtEnd is null ? null : ExpandFilterPaths(filterPathsAtEnd).ToArray();
            var acceptingFilterPaths = pathsAtEnd?
                .Where(path => path.Values.LastOrDefault().IsZero != true).ToArray();
            var rejectingFilterPaths = pathsAtEnd?
                .Where(path => path.Values.LastOrDefault().IsOne != true).ToArray();
            var hasOrdinaryFilterPathOnly = pathsAtEnd is { Length: 0 };
            if (!state.Invalid && state.SyntheticHandler is null && node.Instruction?.Op == OpCodes.Endfilter
                && (acceptingFilterPaths is null || acceptingFilterPaths.Length > 0 || hasOrdinaryFilterPathOnly)
                && graph.FilterHandlerFor(index) is { } handler && graph.Seeds.TryGetValue(handler, out var handlerEntry))
            {
                var outgoing = ApplyUnwindEffects(state with
                {
                    ThisArgumentIsOriginal = OriginalReceiverFor(acceptingFilterPaths, state.ThisArgumentIsOriginal),
                    FilterPaths = acceptingFilterPaths,
                }, finalizerEffects, (unwind, incoming) => EnterUnwindHandler(index, unwind, incoming));
                if (outgoing is not null)
                {
                    var targetState = handlerEntry with
                    {
                        ThisArgumentIsOriginal = outgoing.ThisArgumentIsOriginal,
                        FilterPaths = EnterExceptionRegion(outgoing.FilterPaths, handlerEntry),
                    };
                    Propagate(handler, index, WithExceptional(targetState, null, null));
                }
            }

            if (!state.Invalid && state.SyntheticHandler is null && node.Instruction?.Op == OpCodes.Endfilter
                && (rejectingFilterPaths is null || rejectingFilterPaths.Length > 0 || hasOrdinaryFilterPathOnly))
            {
                ContinueFilterSearch(index, rejectingFilterPaths,
                    OriginalReceiverFor(rejectingFilterPaths, state.ThisArgumentIsOriginal),
                    state.PendingUnwindHandlers);
            }

            if (!state.Invalid && state.SyntheticHandler is null && InsideFilter(graph, index)
                && (node.EffectUnknown || node.Instruction is { } filterInstruction && CanThrow(filterInstruction)))
            {
                ContinueFilterSearch(index, state.FilterPaths, state.ThisArgumentIsOriginal,
                    state.PendingUnwindHandlers);
            }

            var propagatedSwitchTargets = node.Instruction?.Op == OpCodes.Switch ? new HashSet<int>() : null;
            foreach (var edge in graph.Edges[index])
            {
                if (propagatedSwitchTargets is not null && !propagatedSwitchTargets.Add(edge.Target))
                {
                    continue;
                }

                if (state.SyntheticHandler is { } synthetic
                    && !graph.Regions[edge.Target].Contains(synthetic))
                {
                    continue;
                }

                var edgeFilterPaths = state.FilterPaths;
                if (!state.Invalid && node.Instruction is { } branch && transferredValues is not null
                    && branch.Op == OpCodes.Switch)
                {
                    edgeFilterPaths = TransferFilterPaths(SelectSwitchTargetPaths(
                        filterPathsBeforeInstruction, branch, graph.Edges[index], edge.Target,
                        node.Targets.Count), branch, transferredPops, transferredPushes, transferredValues);
                }
                else if (!state.Invalid && node.Instruction is { } booleanBranch && transferredValues is not null
                    && IsConditionedBranch(booleanBranch)
                    && !graph.Edges[index].Any(other => other.Target == edge.Target
                        && other.IsExplicit != edge.IsExplicit))
                {
                    edgeFilterPaths = TransferFilterPaths(
                        SelectBranchPaths(filterPathsBeforeInstruction, booleanBranch, edge, 0),
                        booleanBranch, transferredPops, transferredPushes, transferredValues);
                }

                var outgoing = (FlowState<T>?)(edge.ClearsStack && !state.Invalid
                    ? state with { Values = [], FilterPaths = ClearFilterPathStacks(edgeFilterPaths) }
                    : state with { FilterPaths = edgeFilterPaths });
                if (edge.ClearsStack)
                {
                    outgoing = QueueUnwindHandlers(outgoing!, graph.FinalizersForTransfer(index, edge.Target),
                        finalizerEffects);
                    outgoing = ApplyUnwindEffects(outgoing, finalizerEffects,
                        (handler, incoming) => EnterUnwindHandler(index, handler, incoming));
                }

                if (outgoing is not null)
                {
                    Propagate(edge.Target, index, outgoing);
                }
            }

            if (!state.Invalid && graph.Sections.Count > 0
                && (node.EffectUnknown || node.Instruction is { } exceptionInstruction && CanThrow(exceptionInstruction)))
            {
                foreach (var target in graph.ExceptionSearchTargets(index))
                {
                    if (state.SyntheticHandler is { } synthetic
                        && !graph.Regions[target].Contains(synthetic))
                    {
                        continue;
                    }

                    if (InsideFilter(graph, index) && !SharesFilterRegion(graph, index, target))
                    {
                        continue;
                    }

                    var outgoing = QueueUnwindHandlers(state,
                        graph.UnwindHandlersForException(index, target), finalizerEffects);
                    if (graph.ClauseKindAt(target) != BlockKind.Filter)
                    {
                        outgoing = ApplyUnwindEffects(outgoing, finalizerEffects,
                            (handler, incoming) => EnterUnwindHandler(index, handler, incoming));
                        if (outgoing is null)
                        {
                            continue;
                        }
                    }

                    var entry = graph.Seeds[target] with
                    {
                        ThisArgumentIsOriginal = outgoing.ThisArgumentIsOriginal,
                        FilterPaths = EnterExceptionRegion(outgoing.FilterPaths, graph.Seeds[target]),
                    };
                    Propagate(target, -index - 2, WithExceptional(entry,
                        outgoing.PendingUnwindHandlers, state.SyntheticHandler));
                }
            }
        }

        if (deferredEntries is not null)
        {
            var pendingEntries = deferredEntries;
            deferredEntries = null;
            foreach (var (position, seed) in pendingEntries)
            {
                if (before[position - start] is null)
                {
                    Propagate(position, -1, tracksFilterPaths ? StartFilterPaths(seed) : seed);
                }
            }

            goto AnalyzeQueue;
        }

        if (validateGraph)
        {
            graph.ValidateStacks(before);
        }

        yield return new FlowResult<T>(before, after,
            [.. validateGraph ? graph.Diagnostics : [],
                .. diagnostics.OrderBy(pair => pair.Key.Item1).ThenBy(pair => pair.Key.Item2).Select(pair => pair.Value)],
            maxStack);
    }

    private static (bool Completes, FilterPathState[] Transformations) FinalizerEffect(
        FlowGraph<T> graph, int sectionId, FlowRegion section, FlowResult<T> result)
    {
        var transformations = new List<FilterPathState>();
        var completes = false;
        for (var index = section.Start; index < section.End; index++)
        {
            if (result.After[index - section.Start] is not { Invalid: false } state)
            {
                continue;
            }

            var op = graph.Nodes[index].Instruction?.Op;
            var explicitEnd = op == OpCodes.Endfinally && graph.Regions[index].LastOrDefault(-1) == sectionId;
            var fallsThrough = index + 1 == section.End && (op is null
                || op.Value.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw)
                    && op != OpCodes.Jmp);
            if (!explicitEnd && !fallsThrough)
            {
                continue;
            }

            completes = true;
            if (state.FilterPaths is not { } paths)
            {
                transformations.Add(new FilterPathState([], null,
                    new Dictionary<int, FilterPathValue> { [0] = new(null, null, false) }, false,
                    StackUnknown: true));
                continue;
            }

            foreach (var path in ExpandFilterPaths(paths))
            {
                var transformation = path with
                {
                    Values = [],
                    PendingUnwindEffect = null,
                    StackUnknown = true,
                };
                if (!transformations.Any(candidate => SameFilterPath(candidate, transformation)))
                {
                    transformations.Add(transformation);
                }
            }
        }

        return (completes,
            transformations.Count > MaxFilterPaths ? CollapseFilterPaths([.. transformations]) : [.. transformations]);
    }

    private static FlowState<T> QueueUnwindHandlers(FlowState<T> state, IEnumerable<int> handlers,
        Dictionary<int, (bool Completes, FilterPathState[] Transformations)>? effects)
    {
        var additions = handlers.ToArray();
        var pending = AppendUnwindHandlers(state.PendingUnwindHandlers, additions);
        var queued = state with
        {
            FilterPaths = state.FilterPaths is null ? null :
            [
                .. ExpandFilterPaths(state.FilterPaths).Select(path => path with
                {
                    PendingUnwindEffect = AppendUnwindEffect(path.PendingUnwindEffect, additions, effects),
                }),
            ],
        };
        return WithExceptional(queued, pending, state.SyntheticHandler);
    }

    private static PendingUnwindEffect? AppendUnwindEffect(PendingUnwindEffect? current,
        IEnumerable<int> handlers,
        Dictionary<int, (bool Completes, FilterPathState[] Transformations)>? effects)
    {
        var transformations = current?.Transformations
            ?? IdentityReceiverTransformations;
        var entries = current?.HandlerEntries.ToDictionary() ?? [];
        var boundOutputs = current is { CorrelationLost: false } ? current.BoundOutputs : null;
        var boundEntries = current is { CorrelationLost: false, BoundHandlerEntries: not null }
            ? current.BoundHandlerEntries.ToDictionary() : null;
        var changed = false;
        foreach (var handler in handlers)
        {
            if (transformations.Length == 0)
            {
                break;
            }

            if (entries.ContainsKey(handler))
            {
                continue;
            }

            entries.Add(handler, transformations);
            if (boundOutputs is not null)
            {
                boundEntries ??= [];
                boundEntries.Add(handler, boundOutputs);
            }
            changed = true;
            var effectTransformations = effects is not null && effects.TryGetValue(handler, out var effect)
                && effect.Completes ? effect.Transformations : null;
            transformations = effectTransformations is not null
                ? ComposeReceiverTransformations(transformations, effectTransformations) : [];
            if (boundOutputs is not null)
            {
                boundOutputs = effectTransformations is not null
                    ? ApplyReceiverTransformations(boundOutputs, effectTransformations) : [];
            }
        }

        return changed ? new PendingUnwindEffect(transformations, entries, boundOutputs, boundEntries,
            current?.CorrelationLost == true) : current;
    }

    private static FlowState<T>? ApplyUnwindEffects(FlowState<T>? state,
        Dictionary<int, (bool Completes, FilterPathState[] Transformations)>? effects,
        Action<int, FlowState<T>>? enterHandler = null)
    {
        if (state is null || state.PendingUnwindHandlers is not { Count: > 0 } pending)
        {
            return state;
        }

        if (state.FilterPaths is { Length: 0 })
        {
            foreach (var handler in pending)
            {
                enterHandler?.Invoke(handler, state);
                if (!UnwindCompletes(handler, effects))
                {
                    return null;
                }
            }

            return WithExceptional(state, null, state.SyntheticHandler);
        }

        if (state.FilterPaths is { } paths)
        {
            var completed = new List<FilterPathState>();
            var handlerInputs = new Dictionary<int, List<FilterPathState>>();
            foreach (var path in ExpandFilterPaths(paths))
            {
                if (path.PendingUnwindEffect is not { } effect)
                {
                    AppendFilterPaths(completed, [path]);
                    continue;
                }

                var bound = !effect.CorrelationLost && effect.BoundOutputs is not null;
                var entries = bound ? effect.BoundHandlerEntries! : effect.HandlerEntries;
                foreach (var (handler, transformations) in entries)
                {
                    if (!handlerInputs.TryGetValue(handler, out var inputs))
                    {
                        inputs = [];
                        handlerInputs.Add(handler, inputs);
                    }

                    AppendFilterPaths(inputs, bound ? transformations : ApplyReceiverTransformations(
                        [path], transformations));
                }

                if (effect.CorrelationLost && entries.Count == 0)
                {
                    var transformations = effect.Transformations.Length == 0
                        ? [UnknownUnwindTransformation([path])] : effect.Transformations;
                    var uncertain = ApplyReceiverTransformations([path], transformations);
                    foreach (var handler in pending)
                    {
                        if (!handlerInputs.TryGetValue(handler, out var inputs))
                        {
                            inputs = [];
                            handlerInputs.Add(handler, inputs);
                        }

                        AppendFilterPaths(inputs, uncertain);
                    }
                }

                AppendFilterPaths(completed, bound ? effect.BoundOutputs! : ApplyReceiverTransformations(
                    [path], effect.Transformations));
            }

            foreach (var (handler, inputs) in handlerInputs)
            {
                var boundedInputs = BoundFilterPaths(inputs);
                var handlerState = state with
                {
                    ThisArgumentIsOriginal = OriginalReceiverFor(boundedInputs, state.ThisArgumentIsOriginal),
                    FilterPaths = boundedInputs,
                };
                enterHandler?.Invoke(handler, WithExceptional(handlerState, null, state.SyntheticHandler));
            }

            var boundedCompleted = BoundFilterPaths(completed);
            var result = state with
            {
                ThisArgumentIsOriginal = OriginalReceiverFor(boundedCompleted, state.ThisArgumentIsOriginal),
                FilterPaths = boundedCompleted,
            };
            if (completed.Count > 0)
            {
                return WithExceptional(result, null, state.SyntheticHandler);
            }

            foreach (var handler in pending)
            {
                if (!UnwindCompletes(handler, effects))
                {
                    return null;
                }

                if (!handlerInputs.ContainsKey(handler))
                {
                    enterHandler?.Invoke(handler, state with { FilterPaths = [] });
                }
            }

            return WithExceptional(result, null, state.SyntheticHandler);
        }

        var aggregateThis = state.ThisArgumentIsOriginal;
        foreach (var handler in pending)
        {
            enterHandler?.Invoke(handler, state with { ThisArgumentIsOriginal = aggregateThis });

            if (effects is null || !effects.TryGetValue(handler, out var effect)
                || !effect.Completes)
            {
                return null;
            }

            aggregateThis &= EffectPreservesThis(effect.Transformations);
        }

        return WithExceptional(state with
        {
            ThisArgumentIsOriginal = aggregateThis,
        }, null, state.SyntheticHandler);
    }

    private static bool UnwindCompletes(int handler,
        Dictionary<int, (bool Completes, FilterPathState[] Transformations)>? effects) =>
        effects is not null && effects.TryGetValue(handler, out var effect) && effect.Completes;

    private static bool OriginalReceiverFor(FilterPathState[]? paths, bool fallback) =>
        paths is { Length: > 0 } ? paths.All(path => path.ThisArgumentIsOriginal) : fallback;

    private static void AddFilterPaths(List<FilterPathState> target, IEnumerable<FilterPathState> paths)
    {
        foreach (var path in ExpandFilterPaths(paths))
        {
            AddFilterPath(target, path);
        }
    }

    private static void AppendFilterPaths(List<FilterPathState> target, IEnumerable<FilterPathState> paths) =>
        target.AddRange(ExpandFilterPaths(paths));

    private static FilterPathState[] BoundFilterPaths(List<FilterPathState> paths) =>
        paths.Count > MaxFilterPaths ? CollapseFilterPaths([.. paths]) : [.. paths];

    private static IEnumerable<FilterPathState> ExpandFilterPaths(IEnumerable<FilterPathState> paths)
    {
        foreach (var path in paths)
        {
            if (path.CorrelatedAlternatives is null or { Length: 0 })
            {
                yield return path.CorrelatedAlternatives is null
                    ? path : path with { CorrelatedAlternatives = null };
                continue;
            }

            foreach (var alternative in ExpandFilterPaths(path.CorrelatedAlternatives))
            {
                yield return alternative;
            }
        }
    }

    private static void AddFilterPath(List<FilterPathState> target, FilterPathState path)
    {
        if (target.Any(candidate => SameFilterPathForAccumulation(candidate, path)
            || candidate.CorrelatedAlternatives?.Any(alternative =>
                SameFilterPathForAccumulation(alternative, path)) == true))
        {
            return;
        }

        if (target.Count >= MaxFilterPaths)
        {
            if (target[^1].CorrelatedAlternatives is { } alternatives)
            {
                if (alternatives.Length > 0 && alternatives.Length < MaxCorrelatedAlternatives
                    && (alternatives.Length < MaxFilterPaths
                        || alternatives.All(alternative => ReceiverConditionsExcludeEachOther(
                            alternative.ReceiverConditions, path.ReceiverConditions))))
                {
                    target[^1] = target[^1] with { CorrelatedAlternatives = [.. alternatives, path] };
                    return;
                }

                var inputs = alternatives.Length == 0
                    ? new[] { target[^1] with { CorrelatedAlternatives = null }, path }
                    : [.. alternatives, path];
                target[^1] = CollapseFilterPathGroup(inputs, widenUnwind: true) with
                {
                    CorrelatedAlternatives = [],
                };
                return;
            }

            var collapsed = CollapseFilterPaths([.. target, path]);
            target.Clear();
            target.AddRange(collapsed);
            return;
        }

        target.Add(path);
    }

    private static bool SameFilterPathForAccumulation(FilterPathState left, FilterPathState right) =>
        ReferenceEquals(left.ReceiverConditions, right.ReceiverConditions) && SameFilterPath(left, right);

    private static FilterPathState[] CollapseFilterPaths(FilterPathState[] paths)
    {
        paths = [.. ExpandFilterPaths(paths)];
        var groups = new List<List<FilterPathState>>();
        foreach (var path in paths)
        {
            var group = groups.FirstOrDefault(candidate =>
                candidate[0].ThisArgumentIsOriginal == path.ThisArgumentIsOriginal
                && candidate[0].StackUnknown == path.StackUnknown
                && SameFilterStackFacts(candidate[0].Values, path.Values)
                && SameReceiverConditions(candidate[0].ReceiverConditions, path.ReceiverConditions)
                && SameUnwindResult(candidate[0].PendingUnwindEffect, path.PendingUnwindEffect));
            if (group is null)
            {
                groups.Add([path]);
            }
            else
            {
                group.Add(path);
            }
        }

        var collapsed = groups.Select(group => CollapseFilterPathGroup(group)).ToArray();
        if (collapsed.Length <= MaxFilterPaths)
        {
            return collapsed;
        }

        var result = collapsed.Take(MaxFilterPaths - 1).ToList();
        var overflow = collapsed.Skip(MaxFilterPaths - 1).ToArray();
        var summary = CollapseFilterPathGroup(overflow,
            bindUnwind: overflow.Any(path => path.PendingUnwindEffect is not null));
        result.Add(summary.CorrelatedAlternatives is null
            ? summary with { CorrelatedAlternatives = [] } : summary);
        return [.. result];
    }

    private static bool SameFilterStackFacts(FilterPathValue[] left, FilterPathValue[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair => pair.First.IsZero == pair.Second.IsZero
            && pair.First.IsOne == pair.Second.IsOne && pair.First.IsThis == pair.Second.IsThis
            && pair.First.IntegerValue == pair.Second.IntegerValue
            && pair.First.ComparedSource == pair.Second.ComparedSource
            && pair.First.ComparedInteger == pair.Second.ComparedInteger
            && pair.First.IsNull == pair.Second.IsNull
            && pair.First.ComparedOtherSource == pair.Second.ComparedOtherSource
            && pair.First.ComparedWithNull == pair.Second.ComparedWithNull
            && pair.First.MayBeNaN == pair.Second.MayBeNaN
            && pair.First.IsNaN == pair.Second.IsNaN
            && SameSet(pair.First.ExcludedIntegers, pair.Second.ExcludedIntegers)
            && SameSet(pair.First.EqualSources, pair.Second.EqualSources)
            && SameSet(pair.First.ExcludedSources, pair.Second.ExcludedSources));

    private static FilterPathState CollapseFilterPathGroup(IReadOnlyList<FilterPathState> paths,
        bool bindUnwind = false, bool widenUnwind = false)
    {
        var knownStack = paths.All(path => !path.StackUnknown)
            && paths.Select(path => path.Values.Length).Distinct().Count() == 1;
        var values = knownStack
            ? Enumerable.Range(0, paths[0].Values.Length)
                .Select(index => CollapseFilterValue(paths.Select(path => path.Values[index]))).ToArray()
            : [];
        return new FilterPathState(values,
            CollapseReceiverMappings(paths, locals: true), CollapseReceiverMappings(paths, locals: false),
            paths.All(path => path.ThisArgumentIsOriginal),
            widenUnwind ? WidenUnwindEffects(paths)
                : bindUnwind ? BindUnwindEffects(paths) : CollapseUnwindEffects(paths),
            StackUnknown: !knownStack, ReceiverConditions: CollapseReceiverConditions(paths),
            CorrelatedAlternatives: RetainCorrelatedAlternatives(paths));
    }

    private static PendingUnwindEffect? WidenUnwindEffects(IReadOnlyList<FilterPathState> paths)
    {
        if (paths.All(path => path.PendingUnwindEffect is null))
        {
            return null;
        }

        var transformation = UnknownUnwindTransformation(paths);
        return new PendingUnwindEffect([transformation], new Dictionary<int, FilterPathState[]>(),
            CorrelationLost: true);
    }

    private static FilterPathState UnknownUnwindTransformation(
        IEnumerable<FilterPathState> paths)
    {
        var localKeys = new HashSet<int>();
        var argumentKeys = new HashSet<int> { 0 };
        foreach (var path in ExpandFilterPaths(paths))
        {
            if (path.PendingUnwindEffect is not { } effect)
            {
                continue;
            }

            foreach (var state in ExpandFilterPaths(UnwindReceiverStates(effect)))
            {
                localKeys.UnionWith(state.Locals?.Keys ?? []);
                argumentKeys.UnionWith(state.Arguments?.Keys ?? []);
            }
        }

        var locals = localKeys.Count == 0 ? null : localKeys.ToDictionary(
            key => key, _ => UnknownReceiverValue);
        var arguments = argumentKeys.ToDictionary(key => key, _ => UnknownReceiverValue);
        return new FilterPathState([], locals, arguments, false, StackUnknown: true);
    }

    private static IEnumerable<FilterPathState> UnwindReceiverStates(PendingUnwindEffect effect)
    {
        foreach (var transformation in effect.Transformations)
        {
            yield return transformation;
        }

        foreach (var entry in effect.HandlerEntries.Values.SelectMany(entries => entries))
        {
            yield return entry;
        }

        foreach (var output in effect.BoundOutputs ?? [])
        {
            yield return output;
        }

        foreach (var entry in effect.BoundHandlerEntries?.Values.SelectMany(entries => entries) ?? [])
        {
            yield return entry;
        }
    }

    private static Dictionary<int, FilterPathValue>? CollapseReceiverConditions(
        IReadOnlyList<FilterPathState> paths)
    {
        if (paths[0].ReceiverConditions is not { } first)
        {
            return null;
        }

        var result = new Dictionary<int, FilterPathValue>();
        foreach (var (source, condition) in first)
        {
            if (paths.Skip(1).All(path => path.ReceiverConditions?.TryGetValue(source, out var candidate) == true
                && SameFilterValue(condition, candidate)))
            {
                result.Add(source, condition);
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static FilterPathState[]? RetainCorrelatedAlternatives(IReadOnlyList<FilterPathState> paths)
    {
        if (paths.Count is < 2 or > MaxCorrelatedAlternatives)
        {
            return null;
        }

        var mutuallyExclusive = paths.Count <= MaxFilterPaths
            || paths.Select((path, index) => (path, index)).All(left =>
                paths.Skip(left.index + 1).All(right => ReceiverConditionsExcludeEachOther(
                    left.path.ReceiverConditions, right.ReceiverConditions)));
        if (!mutuallyExclusive)
        {
            return null;
        }

        return [.. paths.Select(path => path with { CorrelatedAlternatives = null })];
    }

    private static bool ReceiverConditionsExcludeEachOther(
        IReadOnlyDictionary<int, FilterPathValue>? left,
        IReadOnlyDictionary<int, FilterPathValue>? right) => left is not null && right is not null
        && left.Any(pair => right.TryGetValue(pair.Key, out var condition)
            && !ConditionsCompatible(pair.Value, condition));

    private static PendingUnwindEffect BindUnwindEffects(IReadOnlyList<FilterPathState> paths)
    {
        var outputs = new List<FilterPathState>();
        var handlerEntries = new Dictionary<int, List<FilterPathState>>();
        var rawTransformations = new List<FilterPathState>();
        var rawHandlerEntries = new Dictionary<int, List<FilterPathState>>();
        var correlationLost = false;
        foreach (var path in ExpandFilterPaths(paths))
        {
            var effect = path.PendingUnwindEffect;
            AddFilterPaths(rawTransformations, effect?.Transformations ?? IdentityReceiverTransformations);
            MergeHandlerEntries(rawHandlerEntries, effect?.HandlerEntries);
            if (effect?.CorrelationLost == true)
            {
                correlationLost = true;
                continue;
            }

            AppendFilterPaths(outputs, effect?.BoundOutputs ?? ApplyReceiverTransformations(
                [path], effect?.Transformations ?? IdentityReceiverTransformations));
            var entries = effect?.BoundHandlerEntries ?? effect?.HandlerEntries.ToDictionary(pair => pair.Key,
                pair => ApplyReceiverTransformations([path], pair.Value));
            if (entries is not null)
            {
                MergeHandlerEntries(handlerEntries, entries);
            }
        }

        return new PendingUnwindEffect([.. rawTransformations], ToHandlerEntryArrays(rawHandlerEntries),
            correlationLost ? null : BoundFilterPaths(outputs),
            correlationLost ? null : ToHandlerEntryArrays(handlerEntries), correlationLost);
    }

    private static void MergeHandlerEntries(Dictionary<int, List<FilterPathState>> target,
        IReadOnlyDictionary<int, FilterPathState[]>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (handler, inputs) in source)
        {
            if (!target.TryGetValue(handler, out var aggregate))
            {
                aggregate = [];
                target.Add(handler, aggregate);
            }

            AddFilterPaths(aggregate, inputs);
        }
    }

    private static Dictionary<int, FilterPathState[]> ToHandlerEntryArrays(
        Dictionary<int, List<FilterPathState>> entries) => entries.ToDictionary(
        pair => pair.Key, pair => pair.Value.ToArray());

    private static PendingUnwindEffect? CollapseUnwindEffects(IReadOnlyList<FilterPathState> paths)
    {
        if (paths.All(path => path.PendingUnwindEffect is null))
        {
            return null;
        }

        if (paths.Any(path => path.PendingUnwindEffect?.BoundOutputs is not null))
        {
            return BindUnwindEffects(paths);
        }

        var transformations = new List<FilterPathState>();
        var handlerEntries = new Dictionary<int, List<FilterPathState>>();
        foreach (var path in paths)
        {
            var effect = path.PendingUnwindEffect;
            AddFilterPaths(transformations, effect?.Transformations ?? IdentityReceiverTransformations);
            if (effect is null)
            {
                continue;
            }

            foreach (var (handler, entries) in effect.HandlerEntries)
            {
                if (!handlerEntries.TryGetValue(handler, out var aggregate))
                {
                    aggregate = [];
                    handlerEntries.Add(handler, aggregate);
                }

                AddFilterPaths(aggregate, entries);
            }
        }

        return new PendingUnwindEffect([.. transformations], handlerEntries.ToDictionary(
            pair => pair.Key, pair => pair.Value.ToArray()));
    }

    private static FilterPathValue CollapseFilterValue(IEnumerable<FilterPathValue> values)
    {
        var entries = values.ToArray();
        var sources = entries.SelectMany(ReceiverSourcesOf).Distinct().Order().ToArray();
        var hasNonSource = sources.Length > 0 && entries.Any(value =>
            value.HasNonSourceAlternative || !ReceiverSourcesOf(value).Any());
        var comparisons = entries.Select(value => (value.ComparedSource, value.ComparedInteger,
            value.ComparedOtherSource, value.ComparedWithNull)).Distinct().ToArray();
        return new FilterPathValue(
            entries.Select(value => value.IsZero).Distinct().Count() == 1 ? entries[0].IsZero : null,
            entries.Select(value => value.IsOne).Distinct().Count() == 1 ? entries[0].IsOne : null,
            entries.All(value => value.IsThis), sources.Length == 1 ? sources[0] : null,
            sources.Length > 1 ? sources : null, hasNonSource,
            entries.Select(value => value.IntegerValue).Distinct().Count() == 1
                ? entries[0].IntegerValue : null,
            IntersectExcludedIntegers(entries),
            comparisons.Length == 1 ? comparisons[0].ComparedSource : null,
            comparisons.Length == 1 ? comparisons[0].ComparedInteger : null,
            entries.Select(value => value.IsNull).Distinct().Count() == 1 ? entries[0].IsNull : null,
            comparisons.Length == 1 ? comparisons[0].ComparedOtherSource : null,
            comparisons.Length == 1 && comparisons[0].ComparedWithNull,
            IntersectSourceSet(entries, equal: true), IntersectSourceSet(entries, equal: false),
            entries.Any(value => value.MayBeNaN),
            entries.Select(value => value.IsNaN).Distinct().Count() == 1 ? entries[0].IsNaN : null);
    }

    private static int[]? IntersectSourceSet(FilterPathValue[] values, bool equal)
    {
        var first = equal ? values[0].EqualSources : values[0].ExcludedSources;
        if (first is not { Count: > 0 })
        {
            return null;
        }

        var result = first.Where(source => values.Skip(1).All(candidate =>
            (equal ? candidate.EqualSources : candidate.ExcludedSources)?.Contains(source) == true))
            .Distinct().Order().ToArray();
        return result.Length == 0 ? null : result;
    }

    private static long[]? IntersectExcludedIntegers(FilterPathValue[] values)
    {
        if (values[0].ExcludedIntegers is not { Count: > 0 } first)
        {
            return null;
        }

        var result = first.Where(value => values.Skip(1)
            .All(candidate => candidate.ExcludedIntegers?.Contains(value) == true)).Distinct().Order().ToArray();
        return result.Length == 0 ? null : result;
    }

    private static Dictionary<int, FilterPathValue>? CollapseReceiverMappings(
        IReadOnlyList<FilterPathState> paths, bool locals)
    {
        var keys = paths.SelectMany(path => (locals ? path.Locals : path.Arguments)?.Keys ?? []).Distinct().ToArray();
        if (keys.Length == 0)
        {
            return null;
        }

        var result = new Dictionary<int, FilterPathValue>();
        foreach (var key in keys)
        {
            var values = paths.Select(path =>
            {
                var mappings = locals ? path.Locals : path.Arguments;
                return mappings?.TryGetValue(key, out var value) == true ? value
                    : new FilterPathValue(null, null, !locals && key == 0 && path.ThisArgumentIsOriginal,
                        locals ? ~key : key);
            }).ToArray();
            result[key] = CollapseFilterValue(values);
        }

        return result;
    }

    private static FilterPathValue ReceiverFrom(FilterPathState path, FilterPathValue source)
    {
        var candidates = ReceiverSourcesOf(source)
            .Select(value => ReceiverFrom(path, value)).ToList();
        if (candidates.Count == 0 || source.HasNonSourceAlternative)
        {
            candidates.Add(new FilterPathValue(source.IsZero, source.IsOne, source.IsThis,
                IntegerValue: source.IntegerValue, ExcludedIntegers: source.ExcludedIntegers,
                ComparedSource: source.ComparedSource, ComparedInteger: source.ComparedInteger,
                IsNull: source.IsNull, ComparedOtherSource: source.ComparedOtherSource,
                ComparedWithNull: source.ComparedWithNull, EqualSources: source.EqualSources,
                ExcludedSources: source.ExcludedSources, MayBeNaN: source.MayBeNaN,
                IsNaN: source.IsNaN));
        }

        return CollapseFilterValue(candidates);
    }

    private static FilterPathValue ReceiverFrom(FilterPathState path, int source)
    {
        if (path.Arguments?.TryGetValue(source, out var value) == true)
        {
            return value;
        }

        if (source < 0 && path.Locals?.TryGetValue(~source, out value) == true)
        {
            return value;
        }

        return new FilterPathValue(null, null, source == 0 && path.ThisArgumentIsOriginal, source);
    }

    private static bool TryApplyReceiverConditions(FilterPathState path,
        IReadOnlyDictionary<int, FilterPathValue>? required,
        out IReadOnlyDictionary<int, FilterPathValue>? result)
    {
        result = path.ReceiverConditions;
        if (required is null)
        {
            return true;
        }

        foreach (var (input, condition) in required)
        {
            var actual = ReceiverFrom(path, input);
            var mappedCondition = RemapReceiverCondition(path, condition);
            if (!ConditionsCompatible(actual, mappedCondition))
            {
                return false;
            }

            var sources = ReceiverSourcesOf(actual).ToArray();
            if (sources is [var source] && !actual.HasNonSourceAlternative
                && !TryAddReceiverCondition(result, source, mappedCondition, out result))
            {
                return false;
            }
        }

        return true;
    }

    private static FilterPathValue RemapReceiverCondition(FilterPathState path, FilterPathValue condition) =>
        condition with
        {
            EqualSources = RemapReceiverSources(path, condition.EqualSources),
            ExcludedSources = RemapReceiverSources(path, condition.ExcludedSources),
        };

    private static int[]? RemapReceiverSources(FilterPathState path, IReadOnlyList<int>? sources)
    {
        if (sources is not { Count: > 0 })
        {
            return null;
        }

        var result = new List<int>();
        foreach (var source in sources)
        {
            var mapped = ReceiverSourcesOf(ReceiverFrom(path, source)).ToArray();
            if (mapped is not [var single])
            {
                return null;
            }

            result.Add(single);
        }

        return [.. result.Distinct().Order()];
    }

    private static bool TryApplyReceiverTransformation(FilterPathState path,
        FilterPathState transformation, out FilterPathState result)
    {
        result = path;
        if (!TryApplyReceiverConditions(path, transformation.ReceiverConditions, out var conditions))
        {
            return false;
        }

        var arguments = path.Arguments is null ? null : new Dictionary<int, FilterPathValue>(path.Arguments);
        if (transformation.Arguments is { } argumentTransformations)
        {
            foreach (var (target, value) in argumentTransformations)
            {
                arguments ??= [];
                arguments[target] = ReceiverFrom(path, value);
            }
        }

        var locals = path.Locals is null ? null : new Dictionary<int, FilterPathValue>(path.Locals);
        if (transformation.Locals is { } localTransformations)
        {
            foreach (var (target, value) in localTransformations)
            {
                locals ??= [];
                locals[target] = ReceiverFrom(path, value);
            }
        }

        var original = arguments?.TryGetValue(0, out var receiver) == true
            ? receiver.IsThis : path.ThisArgumentIsOriginal;
        result = path with
        {
            Arguments = arguments,
            Locals = locals,
            ThisArgumentIsOriginal = original,
            PendingUnwindEffect = null,
            ReceiverConditions = conditions,
        };
        return true;
    }

    private static FilterPathState[] ComposeReceiverTransformations(FilterPathState[] preceding,
        FilterPathState[] following)
    {
        var result = new List<FilterPathState>();
        foreach (var left in ExpandFilterPaths(preceding))
        {
            foreach (var right in ExpandFilterPaths(following))
            {
                if (TryComposeReceiverTransformation(left, right, out var composed))
                {
                    if (!result.Any(candidate => SameFilterPath(candidate, composed)))
                    {
                        result.Add(composed);
                    }
                }
            }
        }

        return result.Count > MaxFilterPaths ? CollapseFilterPaths([.. result]) : [.. result];
    }

    private static FilterPathState[] ApplyReceiverTransformations(FilterPathState[] inputs,
        FilterPathState[] transformations)
    {
        var result = new List<FilterPathState>();
        foreach (var input in ExpandFilterPaths(inputs))
        {
            foreach (var transformation in ExpandFilterPaths(transformations))
            {
                if (TryApplyReceiverTransformation(input, transformation, out var output))
                {
                    if (!result.Any(candidate => SameFilterPath(candidate, output)))
                    {
                        result.Add(output);
                    }
                }
            }
        }

        return result.Count > MaxFilterPaths ? CollapseFilterPaths([.. result]) : [.. result];
    }

    private static bool TryComposeReceiverTransformation(FilterPathState preceding,
        FilterPathState following, out FilterPathState result)
    {
        result = preceding;
        if (!TryApplyReceiverConditions(preceding, following.ReceiverConditions, out var conditions))
        {
            return false;
        }

        var arguments = preceding.Arguments is null ? null
            : new Dictionary<int, FilterPathValue>(preceding.Arguments);
        if (following.Arguments is { } followingArguments)
        {
            foreach (var (target, value) in followingArguments)
            {
                arguments ??= [];
                arguments[target] = SubstituteReceiverSources(preceding, value);
            }
        }

        var locals = preceding.Locals is null ? null
            : new Dictionary<int, FilterPathValue>(preceding.Locals);
        if (following.Locals is { } followingLocals)
        {
            foreach (var (target, value) in followingLocals)
            {
                locals ??= [];
                locals[target] = SubstituteReceiverSources(preceding, value);
            }
        }

        var original = arguments?.TryGetValue(0, out var receiver) != true || receiver.IsThis;
        result = new FilterPathState([], locals, arguments, original,
            ReceiverConditions: conditions);
        return true;
    }

    private static FilterPathValue SubstituteReceiverSources(FilterPathState preceding,
        FilterPathValue value)
    {
        var candidates = new List<FilterPathValue>();
        foreach (var source in ReceiverSourcesOf(value))
        {
            var mappings = source < 0 ? preceding.Locals : preceding.Arguments;
            var key = source < 0 ? ~source : source;
            candidates.Add(mappings?.TryGetValue(key, out var mapped) == true ? mapped
                : new FilterPathValue(null, null, source == 0, source));
        }

        if (candidates.Count == 0 || value.HasNonSourceAlternative)
        {
            candidates.Add(new FilterPathValue(value.IsZero, value.IsOne, value.IsThis,
                IntegerValue: value.IntegerValue, ExcludedIntegers: value.ExcludedIntegers,
                ComparedSource: value.ComparedSource, ComparedInteger: value.ComparedInteger,
                IsNull: value.IsNull, ComparedOtherSource: value.ComparedOtherSource,
                ComparedWithNull: value.ComparedWithNull, EqualSources: value.EqualSources,
                ExcludedSources: value.ExcludedSources, MayBeNaN: value.MayBeNaN,
                IsNaN: value.IsNaN));
        }

        return CollapseFilterValue(candidates);
    }

    private static IEnumerable<int> ReceiverSourcesOf(FilterPathValue value)
    {
        if (value.ReceiverSources is not null)
        {
            return value.ReceiverSources;
        }

        return value.ReceiverSource is { } source ? [source] : [];
    }

    private static bool EffectPreservesThis(FilterPathState[] transformations) => transformations.All(transformation =>
        transformation.Arguments?.TryGetValue(0, out var receiver) != true
        || !receiver.HasNonSourceAlternative && ReceiverSourcesOf(receiver).SequenceEqual([0]));

    private static List<int>? AppendUnwindHandlers(IReadOnlyList<int>? pending,
        IEnumerable<int> handlers)
    {
        var result = pending is null ? [] : new List<int>(pending);
        foreach (var handler in handlers)
        {
            if (!result.Contains(handler))
            {
                result.Add(handler);
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static List<int>? MergeUnwindHandlers(IReadOnlyList<int>? left,
        IReadOnlyList<int>? right) => AppendUnwindHandlers(left, right ?? []);

    private static FlowState<T> WithExceptional(FlowState<T> state,
        IReadOnlyList<int>? pending, int? synthetic)
    {
        if (pending is not null || synthetic is not null)
        {
            return new ExceptionalFlowState<T>(state, pending, synthetic);
        }

        return state is ExceptionalFlowState<T>
            ? new FlowState<T>(state.Values, state.HasUnknownPath, state.Invalid,
                state.ThisArgumentIsOriginal, state.FilterPaths)
            : state;
    }

    private static bool SameUnwindHandlers(IReadOnlyList<int>? left, IReadOnlyList<int>? right) =>
        left is null && right is null || left is not null && right is not null
            && left.SequenceEqual(right);

    private static int? MergeSyntheticHandler(int? left, int? right) => left == right ? left : null;

    private string? ValidateStructure(StackOperandView<T> view, FlowGraph<T> graph, int index)
    {
        var op = view.Op;
        if (op == OpCodes.Ret && graph.Regions[index].Length > 0)
        {
            return "ret is not allowed inside a protected region; use leave to exit it first";
        }

        var usesStaticField = op.Name is "ldsfld" or "ldsflda" or "stsfld";
        var usesInstanceField = op.Name is "ldfld" or "ldflda" or "stfld";
        if (view.FieldIsStatic is { } fieldIsStatic && (usesStaticField || usesInstanceField)
            && usesStaticField != fieldIsStatic)
        {
            return $"{op.Name} cannot access {(fieldIsStatic ? "a static" : "an instance")} field";
        }

        if (view.MethodIsStatic == true && (op == OpCodes.Callvirt || op == OpCodes.Ldvirtftn))
        {
            return $"{op.Name} needs an instance method";
        }

        if (view.MethodIsConstructor == true && op == OpCodes.Callvirt)
        {
            return "callvirt cannot call a constructor; use call";
        }

        if (view.MethodIsAbstract == true && view.MethodIsStatic == false && op == OpCodes.Call)
        {
            return "call cannot invoke an abstract method; use callvirt";
        }

        if (view.MethodIsAbstract == true && view.MethodIsStatic == false && op == OpCodes.Ldftn)
        {
            return "ldftn cannot load an abstract method; use ldvirtftn with a receiver";
        }

        var constrained = graph.Prefixes(index).FirstOrDefault(prefix => prefix.Op == OpCodes.Constrained)?.Type;
        if (constrained is not null && (op == OpCodes.Call || op == OpCodes.Ldftn)
            && view.MethodIsStatic is { } isStatic && view.MethodIsVirtual is { } isVirtual && (!isStatic || !isVirtual))
        {
            return $"constrained. {op.Name} needs a static virtual interface method";
        }

        if (view.MethodIsStatic == true && view.MethodIsVirtual == true
            && (op == OpCodes.Call || op == OpCodes.Ldftn))
        {
            var implementor = constrained is not null && (_types.Algebra.IsValueType(constrained)
                || _types.Algebra.IsGenericParameter(constrained)) ? _types.Algebra.Boxed(constrained) : constrained;
            if (implementor is null || view.DeclaringType is null || !_types.CanAssign(implementor, view.DeclaringType))
            {
                return $"{op.Name} to a static virtual method needs constrained. with a type that implements "
                    + _types.Name(view.DeclaringType);
            }
        }

        if (op == OpCodes.Newobj && (view.MethodIsStatic == true || view.MethodIsConstructor == false))
        {
            return "newobj needs an instance constructor";
        }

        if (op == OpCodes.Newobj && view.DeclaringTypeIsAbstract == true)
        {
            return "newobj cannot create abstract type " + _types.Name(view.DeclaringType);
        }

        if (view.Type is { } operandType && TypeOperandProblem(op, operandType) is { } typeProblem)
        {
            return typeProblem;
        }

        if (op == OpCodes.Jmp && view.JumpRestriction is { } jumpRestriction)
        {
            return jumpRestriction;
        }

        return view.StoreRestriction;
    }

    private string? ValidateStack(StackOperandView<T> view, FlowValue<T>[] values, FlowGraph<T> graph, int index,
        T? returnType, bool cell, bool receiverPathFeasible)
    {
        var op = view.Op;
        var count = values.Length;
        var top = count > 0 ? values[^1].Type : null;
        var kind = top is null ? (StackCategory?)null : _types.Category(top);
        if (op == OpCodes.Ret)
        {
            if (cell)
            {
                if (count > 1)
                {
                    return $"the stack must hold 0 or 1 value at ret, but has {count}: {_types.Render(new FlowState<T>(values))}";
                }

                if (FollowsTailCall(graph, index) && (count == 0 || top is { } returned
                    && (_types.Algebra.IsValueType(returned) || _types.Algebra.IsGenericParameter(returned))
                    && _types.BoxedType(returned) is null))
                {
                    return "a tail call in the cell must return object directly; boxing or a synthesized null before ret is not allowed";
                }

                return top is not null && (_types.Algebra.IsByRef(top) || IsDataPointer(top))
                    ? $"cannot return a {_types.Name(top)} from the cell; load through it first (ldind/ldobj)" : null;
            }

            if (count != (returnType is null ? 0 : 1))
            {
                return returnType is null ? $"ret in void method {graph.BodyName} needs an empty stack"
                    + $" but found {_types.Render(new FlowState<T>(values))} (pop first)"
                    : count == 0 ? $"ret needs {_types.Name(returnType)} on the stack but the stack is empty"
                    : $"ret needs exactly one {_types.Name(returnType)} on the stack but found {_types.Render(new FlowState<T>(values))}";
            }

            if (returnType is not null && !_types.CanAssign(top, returnType))
            {
                var box = top is not null && _types.Algebra.IsValueType(top)
                    && _types.Category(returnType) == StackCategory.ObjectReference ? " (box it first)" : "";
                return $"ret needs {_types.Name(returnType)} on the stack but found {_types.Name(top)}{box}";
            }
        }

        if (op == OpCodes.Switch && kind is not null and not StackCategory.Int32)
        {
            return $"switch needs int32 on the stack but found {_types.Name(top)}";
        }

        if (op.Name is "brtrue" or "brtrue.s" or "brfalse" or "brfalse.s"
            && kind is not (null or StackCategory.Int32 or StackCategory.Int64 or StackCategory.NativeInt or StackCategory.ByRef)
            && !_types.CanAssign(top, _types.Algebra.Primitive("object")))
        {
            return $"{op.Name} needs an integer, pointer, or reference but found {_types.Name(top)}";
        }

        if (op == OpCodes.Endfilter && (count != 1 || kind is not null and not StackCategory.Int32))
        {
            return $"endfilter needs exactly one int32 but found {_types.Render(new FlowState<T>(values))}";
        }

        if (op.Name is { } name
            && (name.StartsWith("stloc", StringComparison.Ordinal) || name.StartsWith("starg", StringComparison.Ordinal))
            && count > 0 && view.SlotType is { } slot && !_types.CanAssign(top, slot))
        {
            return $"{name} needs {_types.Name(slot)} but found {_types.Name(top)}";
        }

        var pops = StackTransfer<T>.PopCount(view);
        if (pops > count)
        {
            return null;
        }

        bool Numeric(T? type) => type is null || _types.Category(type) is StackCategory.Int32 or StackCategory.Int64
            or StackCategory.NativeInt or StackCategory.Float;
        bool Address(T? type) => type is null || _types.Category(type) is StackCategory.ByRef or StackCategory.NativeInt;
        bool Reference(T? type) => type is null || _types.CanAssign(type, _types.Algebra.Primitive("object"));
        bool Receiver(T? type, T? owner)
        {
            if (type is null || owner is null)
            {
                return true;
            }

            if (graph.Prefixes(index).FirstOrDefault(prefix => prefix.Op == OpCodes.Constrained)?.Type is { } constrained)
            {
                return _types.Algebra.IsByRef(type) && _types.Algebra.Same(_types.Algebra.ElementOf(type), constrained)
                    && _types.CanAssign(_types.Algebra.IsValueType(constrained) || _types.Algebra.IsGenericParameter(constrained)
                        ? _types.Algebra.Boxed(constrained) : constrained, owner);
            }

            if (_types.Algebra.IsValueType(owner))
            {
                // ECMA-335 I.12.4.1.4 permits managed, unmanaged, and native-integer pointers to an unboxed value.
                if (_types.Algebra.IsByRef(type) || IsDataPointer(type))
                {
                    return _types.Algebra.Same(_types.Algebra.ElementOf(type), owner);
                }

                return _types.Category(type) == StackCategory.NativeInt;
            }

            return _types.Category(type) == StackCategory.ObjectReference && _types.CanAssign(type, owner);
        }

        bool FieldReceiver(T? type, T? owner)
        {
            if (type is null || owner is null)
            {
                return true;
            }

            if (IsDataPointer(type))
            {
                return _types.CanAssign(_types.Algebra.ElementOf(type), owner);
            }

            if (_types.Category(type) == StackCategory.NativeInt)
            {
                return true;
            }

            if (_types.Algebra.IsByRef(type))
            {
                return _types.Algebra.IsValueType(owner) && _types.Algebra.Same(_types.Algebra.ElementOf(type), owner);
            }

            return _types.CanAssign(type, owner);
        }

        if (op.Name is "add" or "add.ovf" or "add.ovf.un" or "sub" or "sub.ovf" or "sub.ovf.un"
            or "mul" or "mul.ovf" or "mul.ovf.un" or "div" or "div.un" or "rem" or "rem.un"
            or "and" or "or" or "xor" or "shl" or "shr" or "shr.un" || FlowNumericRules.Comparison(op.Name!))
        {
            var left = values[^2].Type;
            var right = values[^1].Type;
            if (left is not null && right is not null
                && !FlowNumericRules.Binary(op.Name!, _types.Category(left), _types.Category(right)))
            {
                return $"{op.Name} cannot combine {_types.Name(left)} and {_types.Name(right)}";
            }

            if (left is not null && right is not null && FlowNumericRules.Comparison(op.Name!)
                && _types.Category(left) == StackCategory.ObjectReference
                && _types.Category(right) == StackCategory.ObjectReference
                && (!Reference(left) || !Reference(right)))
            {
                return $"{op.Name} cannot combine {_types.Name(left)} and {_types.Name(right)}";
            }
        }

        if (op.Name is { } unary && (unary.StartsWith("conv.", StringComparison.Ordinal) || unary is "neg" or "not"))
        {
            var addressConversion = unary is "conv.i" or "conv.u"
                && kind is StackCategory.ByRef or StackCategory.ObjectReference;
            if (op == OpCodes.Conv_R_Un && kind == StackCategory.Float)
            {
                return $"conv.r.un needs an integer value but found {_types.Name(top)}";
            }

            if (!Numeric(top) && !addressConversion || unary == "not" && kind == StackCategory.Float)
            {
                return $"{unary} needs a numeric value but found {_types.Name(top)}";
            }
        }

        if (op == OpCodes.Ckfinite && kind is not (null or StackCategory.Float))
        {
            return $"ckfinite needs a floating-point value but found {_types.Name(top)}";
        }

        if (op.Name is "throw" or "castclass" or "isinst" or "unbox" or "unbox.any"
            && !_types.CanAssign(top, _types.Algebra.Primitive("object")))
        {
            return $"{op.Name} needs an object reference but found {_types.Name(top)}";
        }

        if (op == OpCodes.Jmp && count != 0)
        {
            return "jmp requires an empty evaluation stack";
        }

        if (op == OpCodes.Localloc && (count != 1 || kind is not (null or StackCategory.Int32 or StackCategory.NativeInt)))
        {
            return "localloc needs exactly one integer size on the evaluation stack";
        }

        if (op == OpCodes.Cpblk)
        {
            if (!Address(values[^3].Type) || !Address(values[^2].Type))
            {
                return "cpblk needs destination and source pointers";
            }

            if (values[^1].Type is { } size && _types.Category(size) != StackCategory.Int32)
            {
                return $"cpblk needs an int32 size but found {_types.Name(size)}";
            }
        }

        if (op == OpCodes.Initblk)
        {
            if (!Address(values[^3].Type))
            {
                return $"initblk needs a destination pointer but found {_types.Name(values[^3].Type)}";
            }

            if (values[^2].Type is { } value && _types.Category(value) != StackCategory.Int32)
            {
                return $"initblk needs an int32 value but found {_types.Name(value)}";
            }

            if (values[^1].Type is { } size && _types.Category(size) != StackCategory.Int32)
            {
                return $"initblk needs an int32 size but found {_types.Name(size)}";
            }
        }

        if (graph.Prefixes(index).Any(prefix => prefix.Op == OpCodes.Tailcall) && count != view.ArgumentPops)
        {
            return "a tail call requires exactly its arguments on the evaluation stack";
        }

        if (op.Name is "call" or "callvirt" or "newobj" or "calli")
        {
            var first = count - view.ArgumentPops;
            if (op == OpCodes.Calli)
            {
                if (kind is not (null or StackCategory.NativeInt))
                {
                    return $"calli needs a function pointer but found {_types.Name(top)}";
                }
            }
            if (view.IsInstance && !Receiver(values[first].Type, view.DeclaringType))
            {
                return $"{op.Name} needs a {_types.Name(view.DeclaringType)} receiver but found {_types.Name(values[first].Type)}";
            }
            if (view.HasImplicitThis && !Address(values[first].Type) && !Reference(values[first].Type))
            {
                return $"calli needs a reference or pointer receiver but found {_types.Name(values[first].Type)}";
            }

            for (var parameter = 0; parameter < view.ParameterTypes.Count; parameter++)
            {
                var actual = values[first + (view.IsInstance || view.HasImplicitThis ? 1 : 0) + parameter].Type;
                var expected = view.ParameterTypes[parameter];
                if (!_types.CanAssign(actual, expected))
                {
                    return $"{op.Name} argument {parameter + 1} needs {_types.Name(expected)} but found {_types.Name(actual)}";
                }
            }
        }

        if (op.Name is "stfld" or "stsfld" && view.FieldType is { } field && !_types.CanAssign(top, field))
        {
            return $"{op.Name} needs {_types.Name(field)} but found {_types.Name(top)}";
        }

        if (receiverPathFeasible && view.ReceiverRestriction is { } receiverRestriction && !values[count - pops].IsThis)
        {
            return receiverRestriction;
        }

        if (op.Name is "ldfld" or "ldflda" or "stfld" && !FieldReceiver(values[count - pops].Type, view.DeclaringType))
        {
            return $"{op.Name} needs a {_types.Name(view.DeclaringType)} receiver but found {_types.Name(values[count - pops].Type)}";
        }

        if (op == OpCodes.Ldvirtftn && !Receiver(top, view.DeclaringType))
        {
            return $"ldvirtftn needs a {_types.Name(view.DeclaringType)} receiver but found {_types.Name(top)}";
        }

        if (op == OpCodes.Box && view.Type is { } boxed && !_types.CanAssign(top, boxed))
        {
            return $"box needs {_types.Name(boxed)} but found {_types.Name(top)}";
        }

        if (op == OpCodes.Mkrefany && view.Type is { } referenced && top is { } pointer)
        {
            if (_types.Algebra.IsByRef(pointer) || IsDataPointer(pointer))
            {
                if (!_types.Algebra.Same(_types.Algebra.ElementOf(pointer), referenced))
                {
                    return $"mkrefany needs a pointer to {_types.Name(referenced)} but found {_types.Name(pointer)}";
                }
            }
            else if (_types.Category(pointer) != StackCategory.NativeInt)
            {
                return $"mkrefany needs a pointer to {_types.Name(referenced)} but found {_types.Name(pointer)}";
            }
        }

        if (op.Name is "refanytype" or "refanyval" && top is { } typedReference
            && !_types.Algebra.Same(typedReference, _types.Algebra.Primitive("typedref")))
        {
            return $"{op.Name} needs a typedref but found {_types.Name(typedReference)}";
        }

        if (op.Name is { } memory && (memory.StartsWith("ldind", StringComparison.Ordinal)
            || memory.StartsWith("stind", StringComparison.Ordinal) || memory is "ldobj" or "stobj" or "initobj" or "cpobj"))
        {
            if (!Address(values[count - pops].Type))
            {
                return $"{op.Name} needs a managed or unmanaged pointer but found {_types.Name(values[count - pops].Type)}";
            }

            if (memory == "cpobj" && !Address(top))
            {
                return $"cpobj needs a source pointer but found {_types.Name(top)}";
            }

            var address = values[count - pops].Type;
            var storage = AccessType(view);
            if (memory == "cpobj" && top is { } source && _types.Algebra.IsByRef(source) && storage is not null
                && _types.Algebra.ElementOf(source) is { } sourceType && !_types.CanAssign(sourceType, storage))
            {
                return $"cpobj cannot copy {_types.Name(sourceType)} as {_types.Name(storage)}";
            }

            if (address is not null && _types.Algebra.IsByRef(address) && storage is not null
                && _types.Algebra.ElementOf(address) is { } element
                && !(memory is "ldind.ref" or "stind.ref"
                    ? Reference(element)
                    : memory.StartsWith("ldind", StringComparison.Ordinal) || memory.StartsWith("stind", StringComparison.Ordinal)
                        ? SameIndirectLocation(memory, element, storage)
                    : memory.StartsWith("stind", StringComparison.Ordinal) || memory is "stobj" or "initobj" or "cpobj"
                        ? _types.CanAssign(storage, element) : _types.CanAssign(element, storage)))
            {
                return $"{memory} cannot access {_types.Name(element)} through {_types.Name(address)}";
            }

            if (memory == "stind.ref" && !Reference(top))
            {
                return $"stind.ref needs an object reference but found {_types.Name(top)}";
            }

            if (memory == "stind.ref" && address is not null && _types.Algebra.IsByRef(address)
                && _types.Algebra.ElementOf(address) is { } reference && !_types.CanAssign(top, reference))
            {
                return $"stind.ref needs {_types.Name(reference)} but found {_types.Name(top)}";
            }

            if (storage is not null && (memory.StartsWith("stind", StringComparison.Ordinal) || memory == "stobj")
                && memory != "stind.ref" && !_types.CanAssign(top, storage))
            {
                return $"{memory} needs {_types.Name(storage)} but found {_types.Name(top)}";
            }
        }

        if (op == OpCodes.Newarr && kind is not (null or StackCategory.Int32 or StackCategory.NativeInt))
        {
            return $"newarr needs an integer length but found {_types.Name(top)}";
        }

        if (op.Name is { } arrayOp && (arrayOp.StartsWith("ldelem", StringComparison.Ordinal)
            || arrayOp.StartsWith("stelem", StringComparison.Ordinal) || arrayOp == "ldlen"))
        {
            var array = values[count - pops].Type;
            if (array is not null && !_types.Algebra.IsArray(array) && !_types.Algebra.Same(array, _types.Algebra.NullReference)
                && !_types.Algebra.Same(array, _types.Algebra.UnknownReference))
            {
                return $"{arrayOp} needs an array but found {_types.Name(array)}";
            }

            if (array is not null && _types.Algebra.IsArray(array) && !_types.IsVector(array))
            {
                return $"{arrayOp} needs a zero-based one-dimensional array but found {_types.Name(array)}";
            }

            if (pops > 1 && values[count - pops + 1].Type is { } offset
                && _types.Category(offset) is not (StackCategory.Int32 or StackCategory.NativeInt))
            {
                return $"{arrayOp} needs an integer index but found {_types.Name(offset)}";
            }
            var actualElement = array is not null && _types.Algebra.IsArray(array) ? _types.Algebra.ElementOf(array) : null;
            var instructionElement = ArrayInstructionType(view);
            if (actualElement is not null && arrayOp is ("ldelem.ref" or "stelem.ref") && !Reference(actualElement))
            {
                return $"{arrayOp} needs an array with reference elements but found {_types.Name(array)}";
            }

            if (arrayOp == "stelem.ref" && !Reference(top))
            {
                return $"stelem.ref needs an object reference but found {_types.Name(top)}";
            }

            if (actualElement is not null && instructionElement is not null && arrayOp != "ldlen"
                && arrayOp is not ("ldelem.ref" or "stelem.ref"))
            {
                var mutableAddress = arrayOp == "ldelema"
                    && !graph.Prefixes(index).Any(prefix => prefix.Op == OpCodes.Readonly);
                var nativePointerElement = arrayOp is "ldelem.i" or "stelem.i"
                    && _types.Algebra.IsPointer(actualElement)
                    && _types.Category(instructionElement) == StackCategory.NativeInt;
                var compatibleElement = nativePointerElement || (mutableAddress
                    ? _types.SameVerificationLocation(actualElement, instructionElement)
                    : arrayOp.StartsWith("stelem", StringComparison.Ordinal)
                        ? _types.ArrayElementCompatible(instructionElement, actualElement)
                        : _types.ArrayElementCompatible(actualElement, instructionElement));
                if (!compatibleElement)
                {
                    return $"{arrayOp} cannot access {_types.Name(actualElement)} elements as {_types.Name(instructionElement)}";
                }
            }

            var storedAs = arrayOp == "stelem.ref" ? actualElement : instructionElement;
            if (arrayOp.StartsWith("stelem", StringComparison.Ordinal) && storedAs is not null
                && !_types.CanAssign(top, storedAs))
            {
                return $"{arrayOp} needs {_types.Name(storedAs)} but found {_types.Name(top)}";
            }
        }

        return null;
    }

    private string? TypeOperandProblem(OpCode op, T type)
    {
        if (op.Name is "newarr" or "ldelem" or "ldelema" or "stelem" && !_types.IsArrayElement(type))
        {
            return $"{op.Name} needs an array element type but found {_types.Name(type)}";
        }

        if (op.Name is "box" or "unbox.any" or "castclass" or "isinst" && !_types.IsBoxable(type))
        {
            return $"{op.Name} needs a boxable type but found {_types.Name(type)}";
        }

        if (op == OpCodes.Unbox && (!_types.IsBoxable(type)
            || !_types.Algebra.IsValueType(type) && !_types.Algebra.IsGenericParameter(type)))
        {
            return $"unbox needs a boxable value type or generic parameter but found {_types.Name(type)}";
        }

        if (op == OpCodes.Constrained && (!_types.IsStorageType(type) || _types.Algebra.IsPointer(type)))
        {
            return $"constrained. needs a non-pointer storage type but found {_types.Name(type)}";
        }

        if (op.Name is "ldobj" or "stobj" or "cpobj" or "initobj" or "sizeof" or "mkrefany" or "refanyval"
            && !_types.IsStorageType(type))
        {
            return $"{op.Name} needs a storage type but found {_types.Name(type)}";
        }

        return null;
    }

    private bool UsesNativeAddress(StackOperandView<T> view, FlowValue<T>[] values)
    {
        var name = view.Op.Name;
        if (name is "call" or "callvirt" or "ldvirtftn" && view.IsInstance
            && view.DeclaringType is { } owner && _types.Algebra.IsValueType(owner))
        {
            var callPops = StackTransfer<T>.PopCount(view);
            if (callPops <= values.Length && values[values.Length - callPops].Type is { } receiver)
            {
                return _types.Algebra.IsPointer(receiver)
                    || !_types.Algebra.IsByRef(receiver) && _types.Category(receiver) == StackCategory.NativeInt;
            }
        }

        if (name is null || name is not ("ldobj" or "stobj" or "initobj" or "cpobj" or "ldfld" or "ldflda" or "stfld")
            && !name.StartsWith("ldind", StringComparison.Ordinal)
            && !name.StartsWith("stind", StringComparison.Ordinal))
        {
            return false;
        }

        var pops = StackTransfer<T>.PopCount(view);
        if (pops > values.Length)
        {
            return false;
        }

        var destination = values[values.Length - pops].Type;
        return destination is not null && _types.Category(destination) == StackCategory.NativeInt
            || name == "cpobj" && values[^1].Type is { } source
                && _types.Category(source) == StackCategory.NativeInt;
    }

    private bool UsesGenericReferenceAddress(StackOperandView<T> view, FlowValue<T>[] values)
    {
        if (view.Op.Name is not ("ldind.ref" or "stind.ref"))
        {
            return false;
        }

        var pops = StackTransfer<T>.PopCount(view);
        if (pops > values.Length || values[values.Length - pops].Type is not { } address
            || !_types.Algebra.IsByRef(address))
        {
            return false;
        }

        return _types.Algebra.ElementOf(address) is { } element && _types.Algebra.IsGenericParameter(element);
    }

    private T? AccessType(StackOperandView<T> view)
    {
        if (view.Type is { } type)
        {
            return type;
        }

        var keyword = view.Op.Name?.Split('.').Last() switch
        {
            "i1" => "int8",
            "u1" => "uint8",
            "i2" => "int16",
            "u2" => "uint16",
            "i4" => "int32",
            "u4" => "uint32",
            "i8" => "int64",
            "i" => "native int",
            "r4" => "float32",
            "r8" => "float64",
            "ref" => "object",
            _ => null,
        };
        return keyword is null ? null : _types.Algebra.Primitive(keyword);
    }

    private static bool FollowsTailCall(FlowGraph<T> graph, int index)
    {
        for (var previous = index - 1; previous >= 0; previous--)
        {
            if (graph.Nodes[previous].Instruction is not { } instruction)
            {
                continue;
            }

            return instruction.Op.Name is "call" or "callvirt" or "calli"
                && graph.Prefixes(previous).Any(prefix => prefix.Op == OpCodes.Tailcall);
        }

        return false;
    }

    private bool TailCallPassesManagedPointer(FlowGraph<T> graph, int index, StackOperandView<T> view, FlowValue<T>[] values)
    {
        if (view.Op != OpCodes.Call && view.Op != OpCodes.Callvirt && view.Op != OpCodes.Calli
            || !graph.Prefixes(index).Any(prefix => prefix.Op == OpCodes.Tailcall))
        {
            return false;
        }

        var argumentCount = view.ArgumentPops - (view.Op == OpCodes.Calli ? 1 : 0);
        var first = values.Length - view.ArgumentPops;
        return values.Skip(first).Take(argumentCount)
            .Any(value => value.Type is { } type && _types.Algebra.IsByRef(type));
    }

    private bool IntegerAssignedToPointer(StackOperandView<T> view, FlowValue<T>[] values, T? returnType)
    {
        bool Integer(T? type) => type is not null && !IsDataPointer(type)
            && _types.Category(type) is StackCategory.Int32 or StackCategory.NativeInt;
        bool Pointer(T? type) => type is not null && IsDataPointer(type);
        if (values.Length == 0)
        {
            return false;
        }

        var name = view.Op.Name;
        if (view.Op == OpCodes.Ret)
        {
            return Pointer(returnType) && Integer(values[^1].Type);
        }

        if (name is not null && (name.StartsWith("stloc", StringComparison.Ordinal)
            || name.StartsWith("starg", StringComparison.Ordinal)))
        {
            return Pointer(view.SlotType) && Integer(values[^1].Type);
        }

        if (name is "stfld" or "stsfld")
        {
            return Pointer(view.FieldType) && Integer(values[^1].Type);
        }

        if (name?.StartsWith("stelem", StringComparison.Ordinal) == true)
        {
            var element = view.Type;
            var pops = StackTransfer<T>.PopCount(view);
            if (element is null && values.Length >= pops && values[values.Length - pops].Type is { } array
                && _types.Algebra.IsArray(array))
            {
                element = _types.Algebra.ElementOf(array);
            }

            return Pointer(element) && Integer(values[^1].Type);
        }

        if (view.Op == OpCodes.Stobj)
        {
            return Pointer(view.Type) && Integer(values[^1].Type);
        }

        if (name is not ("call" or "callvirt" or "calli" or "newobj"))
        {
            return false;
        }

        if (values.Length < view.ArgumentPops)
        {
            return false;
        }

        var first = values.Length - view.ArgumentPops + (view.IsInstance || view.HasImplicitThis ? 1 : 0);
        for (var parameter = 0; parameter < view.ParameterTypes.Count; parameter++)
        {
            if (Pointer(view.ParameterTypes[parameter]) && Integer(values[first + parameter].Type))
            {
                return true;
            }
        }

        return false;
    }

    private bool UsesNativePointerArrayInstruction(StackOperandView<T> view, FlowValue<T>[] values)
    {
        if (view.Op.Name is not ("ldelem.i" or "stelem.i"))
        {
            return false;
        }

        var pops = StackTransfer<T>.PopCount(view);
        return pops <= values.Length && values[values.Length - pops].Type is { } array
            && _types.Algebra.IsArray(array) && _types.Algebra.ElementOf(array) is { } element
            && _types.Algebra.IsPointer(element);
    }

    private bool LoadsPointerSlot(StackOperandView<T> view)
    {
        var name = view.Op.Name;
        return name is ("ldloc" or "ldloc.s" or "ldloc.0" or "ldloc.1" or "ldloc.2" or "ldloc.3"
            or "ldarg" or "ldarg.s" or "ldarg.0" or "ldarg.1" or "ldarg.2" or "ldarg.3")
            && view.SlotType is { } slot && IsDataPointer(slot);
    }

    private bool LoadsPointerSlotIndirectly(StackOperandView<T> view, FlowValue<T>[] values)
    {
        if (view.Op != OpCodes.Ldind_I)
        {
            return false;
        }

        var pops = StackTransfer<T>.PopCount(view);
        return pops <= values.Length && values[values.Length - pops].Type is { } address
            && _types.Algebra.IsByRef(address) && _types.Algebra.ElementOf(address) is { } element
            && _types.Algebra.IsPointer(element);
    }

    private bool SameIndirectLocation(string instruction, T element, T storage) =>
        _types.SameVerificationLocation(element, storage)
        || instruction is "ldind.i" or "stind.i" && _types.Algebra.IsPointer(element)
            && _types.Category(storage) == StackCategory.NativeInt;

    private bool TransformsDataPointer(StackOperandView<T> view, FlowValue<T>[] values)
    {
        var name = view.Op.Name;
        return name is ("add" or "sub" or "mul" or "div" or "div.un" or "rem" or "rem.un" or "and" or "or" or "xor"
            or "add.ovf" or "add.ovf.un" or "sub.ovf" or "sub.ovf.un" or "mul.ovf" or "mul.ovf.un"
            or "shl" or "shr" or "shr.un" or "neg" or "not")
            && values.TakeLast(StackTransfer<T>.PopCount(view))
                .Any(value => value.Type is { } type && IsDataPointer(type));
    }

    private bool IsDataPointer(T type) => _types.Algebra.IsPointer(type) && _types.Algebra.ElementOf(type) is not null;

    private T? ArrayInstructionType(StackOperandView<T> view)
    {
        if (view.Type is { } type)
        {
            return type;
        }

        var keyword = view.Op.Name?.Split('.').Last() switch
        {
            "i1" => "int8",
            "u1" => "uint8",
            "i2" => "int16",
            "u2" => "uint16",
            "i4" => "int32",
            "u4" => "uint32",
            "i8" => "int64",
            "i" => "native int",
            "r4" => "float32",
            "r8" => "float64",
            "ref" => "object",
            _ => null,
        };
        return keyword is null ? null : _types.Algebra.Primitive(keyword);
    }

    private static bool UsesReadOnlyAsWritable(StackOperandView<T> view, FlowValue<T>[] values)
    {
        var pops = Math.Min(values.Length, StackTransfer<T>.PopCount(view));
        for (var argument = 0; argument < pops; argument++)
        {
            if (!values[values.Length - pops + argument].IsReadOnly)
            {
                continue;
            }

            var name = view.Op.Name!;
            var permitted = name is "dup" or "pop" || argument == 0
                && (name.StartsWith("ldind", StringComparison.Ordinal) || name is "ldobj" or "ldfld" or "ldflda"
                    || view.IsInstance && name is "call" or "callvirt") || name == "cpobj" && argument == 1;
            if (!permitted)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryMerge(FlowState<T> left, FlowState<T> right, out FlowState<T> merged)
    {
        merged = left;
        if (left.Invalid || right.Invalid)
        {
            merged = WithExceptional(new FlowState<T>(null, Invalid: true,
                ThisArgumentIsOriginal: left.ThisArgumentIsOriginal && right.ThisArgumentIsOriginal),
                MergeUnwindHandlers(left.PendingUnwindHandlers, right.PendingUnwindHandlers),
                MergeSyntheticHandler(left.SyntheticHandler, right.SyntheticHandler));
            return true;
        }

        var unknown = left.HasUnknownPath || right.HasUnknownPath;
        var leftReceiverPathFeasible = left.FilterPaths is not { Length: 0 };
        var rightReceiverPathFeasible = right.FilterPaths is not { Length: 0 };
        bool MergeReceiverFact(bool leftFact, bool rightFact) => (leftReceiverPathFeasible, rightReceiverPathFeasible) switch
        {
            (true, true) => leftFact && rightFact,
            (true, false) => leftFact,
            (false, true) => rightFact,
            _ => leftFact && rightFact,
        };
        var thisArgumentIsOriginal = MergeReceiverFact(left.ThisArgumentIsOriginal, right.ThisArgumentIsOriginal);
        var filterPaths = MergeFilterPaths(left.FilterPaths, right.FilterPaths);
        if (left.Values is not { } a || right.Values is not { } b)
        {
            merged = WithExceptional(new FlowState<T>(left.Values ?? right.Values, unknown,
                ThisArgumentIsOriginal: thisArgumentIsOriginal, FilterPaths: filterPaths),
                MergeUnwindHandlers(left.PendingUnwindHandlers, right.PendingUnwindHandlers),
                MergeSyntheticHandler(left.SyntheticHandler, right.SyntheticHandler));
            return true;
        }

        if (a.Length != b.Length)
        {
            return false;
        }

        var values = new FlowValue<T>[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            if (!_types.TryMerge(a[i].Type, b[i].Type, out var type))
            {
                return false;
            }

            values[i] = new FlowValue<T>(type, [.. a[i].Origins.Union(b[i].Origins).Order()],
                MergeReceiverFact(a[i].IsThis, b[i].IsThis), a[i].IsReadOnly || b[i].IsReadOnly);
        }

        merged = WithExceptional(new FlowState<T>(values, unknown,
            ThisArgumentIsOriginal: thisArgumentIsOriginal, FilterPaths: filterPaths),
            MergeUnwindHandlers(left.PendingUnwindHandlers, right.PendingUnwindHandlers),
            MergeSyntheticHandler(left.SyntheticHandler, right.SyntheticHandler));
        return true;
    }

    private bool Equal(FlowState<T> left, FlowState<T> right)
    {
        if (left.Invalid != right.Invalid || left.HasUnknownPath != right.HasUnknownPath
            || left.ThisArgumentIsOriginal != right.ThisArgumentIsOriginal || left.Values?.Length != right.Values?.Length
            || !SameFilterPaths(left.FilterPaths, right.FilterPaths)
            || !SameUnwindHandlers(left.PendingUnwindHandlers, right.PendingUnwindHandlers)
            || left.SyntheticHandler != right.SyntheticHandler)
        {
            return false;
        }

        if (left.Values is not { } a || right.Values is not { } b)
        {
            return true;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (!_types.Algebra.Same(a[i].Type, b[i].Type) || a[i].IsThis != b[i].IsThis
                || a[i].IsReadOnly != b[i].IsReadOnly || !a[i].Origins.SequenceEqual(b[i].Origins))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InsideFilter(FlowGraph<T> graph, int index) => graph.Regions[index]
        .Any(section => graph.Sections[section].Kind == BlockKind.Filter);

    private static bool SharesFilterRegion(FlowGraph<T> graph, int source, int target)
    {
        var filter = graph.Regions[source].Reverse()
            .FirstOrDefault(section => graph.Sections[section].Kind == BlockKind.Filter, -1);
        return filter >= 0 && graph.Regions[target].Contains(filter);
    }

    private FilterPathState[]? TransferFilterPaths(FilterPathState[]? paths, StackOperandView<T> view,
        int pops, int pushes, FlowValue<T>[] popped, bool bounded = true)
    {
        if (paths is null)
        {
            return null;
        }

        if (paths.Any(path => path.CorrelatedAlternatives is not null))
        {
            var preserved = new List<FilterPathState>();
            foreach (var path in paths)
            {
                if (path.CorrelatedAlternatives is null)
                {
                    var transformed = TransferFilterPaths([path], view, pops, pushes, popped, bounded: false);
                    if (transformed is null)
                    {
                        return null;
                    }

                    preserved.AddRange(transformed);
                    continue;
                }

                var summary = TransferFilterPaths(
                    [path with { CorrelatedAlternatives = null }], view, pops, pushes, popped, bounded: false);
                if (summary is not [var transformedSummary])
                {
                    return null;
                }

                if (path.CorrelatedAlternatives.Length == 0)
                {
                    preserved.Add(transformedSummary with { CorrelatedAlternatives = [] });
                    continue;
                }

                var alternatives = TransferFilterPaths(
                    path.CorrelatedAlternatives, view, pops, pushes, popped, bounded: false);
                if (alternatives is null)
                {
                    return null;
                }

                preserved.Add(transformedSummary with { CorrelatedAlternatives = alternatives });
            }

            return [.. preserved];
        }

        var result = new List<FilterPathState>();
        foreach (var path in ExpandFilterPaths(paths))
        {
            if (path.StackUnknown)
            {
                var unknown = TransferUnknownFilterPath(path, view);
                if (!result.Any(existing => SameFilterPath(existing, unknown)))
                {
                    result.Add(unknown);
                }
                continue;
            }

            if (path.Values.Length < pops)
            {
                return null;
            }

            var pathPopped = path.Values.TakeLast(pops).ToArray();
            var output = path.Values.Take(path.Values.Length - pops).ToList();
            var locals = path.Locals is null ? null : new Dictionary<int, FilterPathValue>(path.Locals);
            var arguments = path.Arguments is null ? null : new Dictionary<int, FilterPathValue>(path.Arguments);
            if (StoresLocal(view) && view.LocalIndex is { } storedLocal)
            {
                locals ??= [];
                locals[storedLocal] = pathPopped[^1];
            }
            else if (StoresArgument(view) && view.ArgumentIndex is { } storedArgument)
            {
                arguments ??= [];
                arguments[storedArgument] = pathPopped[^1];
            }
            else if (LoadsLocalAddress(view) && view.LocalIndex is { } exposedLocal)
            {
                locals ??= [];
                locals[exposedLocal] = new FilterPathValue(null, null, false);
            }
            else if (LoadsArgumentAddress(view) && view.ArgumentIndex is { } exposedArgument)
            {
                arguments ??= [];
                arguments[exposedArgument] = new FilterPathValue(null, null, false);
            }

            var pushed = LoadsLocal(view) && view.LocalIndex is { } loadedLocal
                && locals?.TryGetValue(loadedLocal, out var local) == true ? local
                : LoadsLocal(view) && view.LocalIndex is { } receiverLocal
                    ? ConstantFilterValue(view, false) with
                    {
                        ReceiverSource = ~receiverLocal,
                        ReceiverSources = null,
                        HasNonSourceAlternative = false,
                    }
                : LoadsArgument(view) && view.ArgumentIndex is { } loadedArgument
                    && arguments?.TryGetValue(loadedArgument, out var argument) == true ? argument
                : LoadsArgument(view) && view.ArgumentIndex is { } receiverSource
                    ? ConstantFilterValue(view, view.ReadsThisArgument && path.ThisArgumentIsOriginal) with
                    {
                        ReceiverSource = receiverSource,
                        ReceiverSources = null,
                        HasNonSourceAlternative = false,
                    }
                : PreservesFilterDecision(view, popped)
                    ? pathPopped[^1] with { IsThis = false }
                : ConstantFilterValue(view, view.ReadsThisArgument && path.ThisArgumentIsOriginal);
            if (view.SlotType is { } slotType && _types.Category(slotType) == StackCategory.Float
                && (LoadsLocal(view) || LoadsArgument(view)))
            {
                pushed = pushed with { MayBeNaN = true };
            }
            pushed = RefineComputedFilterValue(view, pathPopped, pushed);
            pushed = ApplyReceiverConditions(pushed, path.ReceiverConditions);
            for (var index = 0; index < pushes; index++)
            {
                output.Add(view.Op == OpCodes.Dup ? pathPopped[0] : pushed);
            }

            if (StackTransfer<T>.EndsPath(view.Op))
            {
                output.Clear();
            }

            var thisArgumentIsOriginal = view.WritesThisArgument ? pathPopped[^1].IsThis
                : view.ReadsThisArgument && view.Op.Name is "ldarga" or "ldarga.s" ? false
                : path.ThisArgumentIsOriginal;
            var pendingEffect = InvalidatesBoundUnwind(view) && path.PendingUnwindEffect?.BoundOutputs is not null
                ? path.PendingUnwindEffect with { CorrelationLost = true }
                : path.PendingUnwindEffect;
            var next = new FilterPathState([.. output], locals, arguments, thisArgumentIsOriginal,
                pendingEffect, ReceiverConditions: path.ReceiverConditions);
            result.Add(next);
        }

        return bounded && result.Count > MaxFilterPaths ? CollapseFilterPaths([.. result]) : [.. result];
    }

    private static FilterPathValue RefineComputedFilterValue(StackOperandView<T> view,
        FilterPathValue[] inputs, FilterPathValue fallback)
    {
        if (view.Op != OpCodes.Ceq || inputs.Length != 2)
        {
            return fallback;
        }

        return EqualityFilterValue(inputs[0], inputs[1], fallback);
    }

    private static FilterPathValue EqualityFilterValue(
        FilterPathValue left, FilterPathValue right, FilterPathValue fallback)
    {
        if (KnownEqual(left, right) is { } equal)
        {
            return new FilterPathValue(!equal, equal, false, IntegerValue: equal ? 1 : 0);
        }

        var leftSources = ReceiverSourcesOf(left).ToArray();
        var rightSources = ReceiverSourcesOf(right).ToArray();
        if (!left.HasNonSourceAlternative && !right.HasNonSourceAlternative
            && leftSources is [var leftSource] && rightSources is [var rightSource])
        {
            return new FilterPathValue(null, null, false,
                ComparedSource: leftSource, ComparedOtherSource: rightSource,
                MayBeNaN: left.MayBeNaN || right.MayBeNaN);
        }

        if (EqualitySource(left, right) is { } comparison)
        {
            return new FilterPathValue(null, null, false,
                ComparedSource: comparison.Source, ComparedInteger: comparison.Integer,
                ComparedWithNull: comparison.WithNull);
        }

        return fallback;
    }

    private static (int Source, long? Integer, bool WithNull)? EqualitySource(
        FilterPathValue left, FilterPathValue right)
    {
        var rightSources = ReceiverSourcesOf(right).ToArray();
        if (left.IntegerValue is { } leftInteger && rightSources is [var rightSource]
            && !right.HasNonSourceAlternative)
        {
            return (rightSource, leftInteger, false);
        }

        if (left.IsNull == true && rightSources is [var nullComparedSource]
            && !right.HasNonSourceAlternative)
        {
            return (nullComparedSource, null, true);
        }

        var leftSources = ReceiverSourcesOf(left).ToArray();
        if (right.IntegerValue is { } rightInteger && leftSources is [var leftSource]
            && !left.HasNonSourceAlternative)
        {
            return (leftSource, rightInteger, false);
        }

        return right.IsNull == true && leftSources is [var otherNullComparedSource]
            && !left.HasNonSourceAlternative ? (otherNullComparedSource, null, true) : null;
    }

    private static bool? KnownEqual(FilterPathValue left, FilterPathValue right)
    {
        if (left.IsNaN == true || right.IsNaN == true)
        {
            return false;
        }

        if (left.IntegerValue is { } leftInteger && right.IntegerValue is { } rightInteger)
        {
            return leftInteger == rightInteger;
        }

        if (left.IsNull == true && right.IsNull is { } rightNull)
        {
            return rightNull;
        }

        if (right.IsNull == true && left.IsNull is { } leftNull)
        {
            return leftNull;
        }

        var leftSources = ReceiverSourcesOf(left).ToArray();
        var rightSources = ReceiverSourcesOf(right).ToArray();
        if (!left.HasNonSourceAlternative && !right.HasNonSourceAlternative
            && leftSources is [var leftSource] && rightSources is [var rightSource])
        {
            if (left.EqualSources?.Contains(rightSource) == true
                || right.EqualSources?.Contains(leftSource) == true)
            {
                return true;
            }

            if (left.ExcludedSources?.Contains(rightSource) == true
                || right.ExcludedSources?.Contains(leftSource) == true)
            {
                return false;
            }

            if (leftSource == rightSource
                && (left.IsNaN == false || !left.MayBeNaN)
                && (right.IsNaN == false || !right.MayBeNaN))
            {
                return true;
            }
        }

        if (left.IntegerValue is { } excludedLeft && right.ExcludedIntegers?.Contains(excludedLeft) == true
            || right.IntegerValue is { } excludedRight && left.ExcludedIntegers?.Contains(excludedRight) == true)
        {
            return false;
        }

        if (left.IntegerValue == 0 && right.IsZero is { } rightZero)
        {
            return rightZero;
        }

        if (right.IntegerValue == 0 && left.IsZero is { } leftZero)
        {
            return leftZero;
        }

        if (left.IntegerValue == 1 && right.IsOne is { } rightOne)
        {
            return rightOne;
        }

        return right.IntegerValue == 1 && left.IsOne is { } leftOne ? leftOne : null;
    }

    private static FilterPathValue ApplyReceiverConditions(FilterPathValue value,
        IReadOnlyDictionary<int, FilterPathValue>? conditions)
    {
        var sources = ReceiverSourcesOf(value).ToArray();
        if (sources is not [var source] || value.HasNonSourceAlternative
            || conditions?.TryGetValue(source, out var condition) != true
            || !TryMergeReceiverCondition(value, condition, out var merged))
        {
            return value;
        }

        return merged with
        {
            IsThis = value.IsThis,
            ReceiverSource = value.ReceiverSource,
            ReceiverSources = value.ReceiverSources,
            HasNonSourceAlternative = value.HasNonSourceAlternative,
            MayBeNaN = value.MayBeNaN,
        };
    }

    private static FilterPathState TransferUnknownFilterPath(FilterPathState path, StackOperandView<T> view)
    {
        var unknown = new FilterPathValue(null, null, false);
        var locals = path.Locals is null ? null : new Dictionary<int, FilterPathValue>(path.Locals);
        var arguments = path.Arguments is null ? null : new Dictionary<int, FilterPathValue>(path.Arguments);
        if (StoresLocal(view) && view.LocalIndex is { } storedLocal)
        {
            locals ??= [];
            locals[storedLocal] = unknown;
        }
        else if (StoresArgument(view) && view.ArgumentIndex is { } storedArgument)
        {
            arguments ??= [];
            arguments[storedArgument] = unknown;
        }
        else if (LoadsLocalAddress(view) && view.LocalIndex is { } exposedLocal)
        {
            locals ??= [];
            locals[exposedLocal] = unknown;
        }
        else if (LoadsArgumentAddress(view) && view.ArgumentIndex is { } exposedArgument)
        {
            arguments ??= [];
            arguments[exposedArgument] = unknown;
        }

        var original = view.WritesThisArgument
            || view.ReadsThisArgument && view.Op.Name is "ldarga" or "ldarga.s"
            ? false : path.ThisArgumentIsOriginal;
        return path with
        {
            Values = [],
            Locals = locals,
            Arguments = arguments,
            ThisArgumentIsOriginal = original,
            StackUnknown = true,
            PendingUnwindEffect = InvalidatesBoundUnwind(view)
                && path.PendingUnwindEffect?.BoundOutputs is not null
                ? path.PendingUnwindEffect with { CorrelationLost = true }
                : path.PendingUnwindEffect,
        };
    }

    private static bool InvalidatesBoundUnwind(StackOperandView<T> view) => StoresLocal(view)
        || StoresArgument(view) || LoadsLocalAddress(view) || LoadsArgumentAddress(view);

    private bool PreservesFilterDecision(StackOperandView<T> view, FlowValue<T>[] popped)
    {
        if (popped is not [{ Type: { } type }] || _types.Category(type) != StackCategory.Int32)
        {
            return false;
        }

        return view.Op == OpCodes.Conv_I4 || view.Op == OpCodes.Conv_U4
            || view.Op == OpCodes.Conv_Ovf_I4 || view.Op == OpCodes.Conv_Ovf_I4_Un
            || view.Op == OpCodes.Conv_Ovf_U4 || view.Op == OpCodes.Conv_Ovf_U4_Un;
    }

    private static bool LoadsLocal(StackOperandView<T> view) => view.Op.Name is
        "ldloc" or "ldloc.s" or "ldloc.0" or "ldloc.1" or "ldloc.2" or "ldloc.3";

    private static bool StoresLocal(StackOperandView<T> view) => view.Op.Name is
        "stloc" or "stloc.s" or "stloc.0" or "stloc.1" or "stloc.2" or "stloc.3";

    private static bool LoadsLocalAddress(StackOperandView<T> view) => view.Op.Name is "ldloca" or "ldloca.s";

    private static bool LoadsArgument(StackOperandView<T> view) => view.Op.Name is
        "ldarg" or "ldarg.s" or "ldarg.0" or "ldarg.1" or "ldarg.2" or "ldarg.3";

    private static bool StoresArgument(StackOperandView<T> view) => view.Op.Name is "starg" or "starg.s";

    private static bool LoadsArgumentAddress(StackOperandView<T> view) => view.Op.Name is "ldarga" or "ldarga.s";

    private static bool IsConditionedBranch(StackOperandView<T> view) => view.Op.Name is
        "brtrue" or "brtrue.s" or "brfalse" or "brfalse.s"
        or "beq" or "beq.s" or "bne.un" or "bne.un.s";

    private static FilterPathState[]? SelectSwitchTargetPaths(FilterPathState[]? paths,
        StackOperandView<T> view, IEnumerable<FlowEdge> edges, int target, int switchCaseCount)
    {
        if (paths is null)
        {
            return null;
        }

        var result = new List<FilterPathState>();
        foreach (var edge in edges.Where(candidate => candidate.Target == target))
        {
            AddFilterPaths(result, SelectBranchPaths(paths, view, edge, switchCaseCount) ?? []);
        }

        return [.. result];
    }

    private static FilterPathState[]? SelectBranchPaths(FilterPathState[]? paths, StackOperandView<T> view,
        FlowEdge edge, int switchCaseCount)
    {
        if (paths is null)
        {
            return null;
        }

        var result = new List<FilterPathState>();
        foreach (var path in ExpandFilterPaths(paths))
        {
            var value = path.Values.LastOrDefault();
            if (view.Op == OpCodes.Switch)
            {
                AddSwitchBranchPaths(result, path, value, edge.SwitchCases, switchCaseCount);
                continue;
            }

            if (view.Op.Name is "beq" or "beq.s" or "bne.un" or "bne.un.s")
            {
                if (path.Values.Length < 2)
                {
                    result.Add(path);
                    continue;
                }

                var comparison = EqualityFilterValue(path.Values[^2], path.Values[^1],
                    new FilterPathValue(null, null, false));
                var branchOnEqual = view.Op.Name is "beq" or "beq.s";
                var requiresEqual = branchOnEqual == edge.IsExplicit;
                AddConditionedPath(result, path, comparison,
                    new FilterPathValue(!requiresEqual, requiresEqual, false,
                        IntegerValue: requiresEqual ? 1 : 0));
                continue;
            }

            var branchesOnTrue = view.Op.Name is "brtrue" or "brtrue.s";
            var requiresZero = branchesOnTrue ? !edge.IsExplicit : edge.IsExplicit;
            var condition = new FilterPathValue(requiresZero, requiresZero ? false : null, false,
                IntegerValue: requiresZero ? 0 : null,
                ExcludedIntegers: requiresZero ? null : [0]);
            AddConditionedPath(result, path, value, condition);
        }

        return [.. result];
    }

    private static void AddSwitchBranchPaths(List<FilterPathState> result, FilterPathState path,
        FilterPathValue value, IReadOnlyList<int>? switchCases, int switchCaseCount)
    {
        if (switchCases is null)
        {
            var excluded = Enumerable.Range(0, switchCaseCount).Select(index => (long)index).ToArray();
            AddConditionedPath(result, path, value,
                new FilterPathValue(switchCaseCount > 0 ? false : null,
                    switchCaseCount > 1 ? false : null, false,
                    ExcludedIntegers: excluded.Length == 0 ? null : excluded));
            return;
        }

        foreach (var @case in switchCases)
        {
            AddConditionedPath(result, path, value,
                new FilterPathValue(@case == 0, @case == 1, false, IntegerValue: @case));
        }
    }

    private static void AddConditionedPath(List<FilterPathState> result, FilterPathState path,
        FilterPathValue value, FilterPathValue condition)
    {
        if (!ConditionsCompatible(value, condition))
        {
            return;
        }

        if (value.ComparedSource is { } comparedSource && EqualityRequirement(condition) is { } equal)
        {
            var sourceCondition = ComparisonCondition(value, equal);
            if (TryAddReceiverCondition(path.ReceiverConditions, comparedSource,
                sourceCondition, out var comparisonConditions))
            {
                result.Add(path with { ReceiverConditions = comparisonConditions });
            }
            return;
        }

        var sources = ReceiverSourcesOf(value).ToArray();
        if (sources is [var source] && !value.HasNonSourceAlternative)
        {
            if (TryAddReceiverCondition(path.ReceiverConditions, source, condition, out var conditions))
            {
                result.Add(path with { ReceiverConditions = conditions });
            }
            return;
        }

        result.Add(path);
    }

    private static FilterPathValue ComparisonCondition(FilterPathValue comparison, bool equal)
    {
        if (comparison.ComparedWithNull)
        {
            return new FilterPathValue(null, null, false, IsNull: equal);
        }

        if (comparison.ComparedOtherSource is { } otherSource)
        {
            if (otherSource == comparison.ComparedSource && comparison.MayBeNaN)
            {
                return new FilterPathValue(null, null, false, IsNaN: !equal);
            }

            return new FilterPathValue(null, null, false,
                EqualSources: equal ? [otherSource] : null,
                ExcludedSources: equal ? null : [otherSource],
                IsNaN: equal && comparison.MayBeNaN ? false : null);
        }

        var integer = comparison.ComparedInteger!.Value;
        return equal
            ? new FilterPathValue(integer == 0, integer == 1, false, IntegerValue: integer)
            : new FilterPathValue(integer == 0 ? false : null,
                integer == 1 ? false : null, false, ExcludedIntegers: [integer]);
    }

    private static bool? EqualityRequirement(FilterPathValue condition)
    {
        if (condition.IntegerValue is 0)
        {
            return false;
        }

        if (condition.IntegerValue is 1 || condition.IsOne == true || condition.IsZero == false)
        {
            return true;
        }

        return condition.IsZero == true ? false : null;
    }

    private static bool TryAddReceiverCondition(IReadOnlyDictionary<int, FilterPathValue>? existing,
        int source, FilterPathValue condition,
        out IReadOnlyDictionary<int, FilterPathValue>? result)
    {
        var conditions = existing is null ? [] : new Dictionary<int, FilterPathValue>(existing);
        if (conditions.TryGetValue(source, out var current))
        {
            if (!TryMergeReceiverCondition(current, condition, out condition))
            {
                result = existing;
                return false;
            }
        }

        conditions[source] = condition;
        return TryNormalizeReceiverConditions(conditions, out result);
    }

    private static bool TryNormalizeReceiverConditions(
        Dictionary<int, FilterPathValue> conditions,
        out IReadOnlyDictionary<int, FilterPathValue>? result)
    {
        var sources = conditions.Keys.Concat(conditions.Values.SelectMany(condition =>
            (condition.EqualSources ?? []).Concat(condition.ExcludedSources ?? []))).Distinct().ToArray();
        var parents = sources.ToDictionary(source => source);

        int Find(int source)
        {
            var root = source;
            while (parents[root] != root)
            {
                root = parents[root];
            }

            while (parents[source] != source)
            {
                var next = parents[source];
                parents[source] = root;
                source = next;
            }

            return root;
        }

        void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot)
            {
                parents[rightRoot] = leftRoot;
            }
        }

        foreach (var (source, condition) in conditions)
        {
            foreach (var equal in condition.EqualSources ?? [])
            {
                Union(source, equal);
            }
        }

        var exclusions = new Dictionary<int, HashSet<int>>();
        foreach (var (source, condition) in conditions)
        {
            foreach (var excluded in condition.ExcludedSources ?? [])
            {
                var sourceRoot = Find(source);
                var excludedRoot = Find(excluded);
                if (sourceRoot == excludedRoot)
                {
                    result = conditions;
                    return false;
                }

                if (!exclusions.TryGetValue(sourceRoot, out var sourceExclusions))
                {
                    sourceExclusions = [];
                    exclusions.Add(sourceRoot, sourceExclusions);
                }
                if (!exclusions.TryGetValue(excludedRoot, out var excludedExclusions))
                {
                    excludedExclusions = [];
                    exclusions.Add(excludedRoot, excludedExclusions);
                }
                sourceExclusions.Add(excludedRoot);
                excludedExclusions.Add(sourceRoot);
            }
        }

        var scalarConditions = new Dictionary<int, FilterPathValue>();
        foreach (var source in sources)
        {
            var scalar = conditions.TryGetValue(source, out var condition)
                ? condition with { EqualSources = null, ExcludedSources = null }
                : new FilterPathValue(null, null, false);
            var root = Find(source);
            if (scalarConditions.TryGetValue(root, out var current)
                && !TryMergeReceiverCondition(current, scalar, out scalar))
            {
                result = conditions;
                return false;
            }
            scalarConditions[root] = scalar;
        }

        var groups = sources.GroupBy(Find).ToDictionary(group => group.Key, group => group.Order().ToArray());
        var normalized = new Dictionary<int, FilterPathValue>();
        foreach (var source in sources)
        {
            var root = Find(source);
            var equalSources = groups[root].Where(candidate => candidate != source).ToArray();
            var excludedSources = exclusions.TryGetValue(root, out var excludedRoots)
                ? excludedRoots.SelectMany(excludedRoot => groups[excludedRoot]).Distinct().Order().ToArray()
                : [];
            normalized[source] = scalarConditions[root] with
            {
                EqualSources = equalSources.Length == 0 ? null : equalSources,
                ExcludedSources = excludedSources.Length == 0 ? null : excludedSources,
            };
        }

        result = normalized;
        return true;
    }

    private static bool TryMergeReceiverCondition(FilterPathValue left, FilterPathValue right,
        out FilterPathValue result)
    {
        result = left;
        if (!ConditionsCompatible(left, right))
        {
            return false;
        }

        var excluded = (left.ExcludedIntegers ?? []).Concat(right.ExcludedIntegers ?? [])
            .Distinct().Order().ToArray();
        var equalSources = (left.EqualSources ?? []).Concat(right.EqualSources ?? [])
            .Distinct().Order().ToArray();
        var excludedSources = (left.ExcludedSources ?? []).Concat(right.ExcludedSources ?? [])
            .Distinct().Order().ToArray();
        result = new FilterPathValue(left.IsZero ?? right.IsZero, left.IsOne ?? right.IsOne, false,
            IntegerValue: left.IntegerValue ?? right.IntegerValue,
            ExcludedIntegers: excluded.Length == 0 ? null : excluded,
            IsNull: left.IsNull ?? right.IsNull,
            EqualSources: equalSources.Length == 0 ? null : equalSources,
            ExcludedSources: excludedSources.Length == 0 ? null : excludedSources,
            MayBeNaN: left.MayBeNaN || right.MayBeNaN,
            IsNaN: left.IsNaN ?? right.IsNaN);
        return true;
    }

    private static bool ConditionsCompatible(FilterPathValue actual, FilterPathValue required)
    {
        var actualZero = actual.IntegerValue is { } actualInteger ? actualInteger == 0 : actual.IsZero;
        var requiredZero = required.IntegerValue is { } requiredInteger ? requiredInteger == 0 : required.IsZero;
        var actualOne = actual.IntegerValue is { } actualOneInteger ? actualOneInteger == 1 : actual.IsOne;
        var requiredOne = required.IntegerValue is { } requiredOneInteger ? requiredOneInteger == 1 : required.IsOne;
        if (actualZero is { } zero && requiredZero is { } neededZero && zero != neededZero
            || actualOne is { } one && requiredOne is { } neededOne && one != neededOne
            || actual.IntegerValue is { } exact && required.IntegerValue is { } needed && exact != needed
            || actual.IntegerValue is { } actualValue && required.ExcludedIntegers?.Contains(actualValue) == true
            || required.IntegerValue is { } requiredValue && actual.ExcludedIntegers?.Contains(requiredValue) == true
            || actual.IsNull is { } actualNull && required.IsNull is { } requiredNull && actualNull != requiredNull
            || actual.IsNaN is { } actualNaN && required.IsNaN is { } requiredNaN && actualNaN != requiredNaN
            || actual.EqualSources?.Any(source => required.ExcludedSources?.Contains(source) == true) == true
            || required.EqualSources?.Any(source => actual.ExcludedSources?.Contains(source) == true) == true)
        {
            return false;
        }

        return true;
    }

    private static bool CanThrow(StackOperandView<T> view)
    {
        var name = view.Op.Name!;
        if (view.DecodedPrefixName is not null || view.Op.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch
            || name is "nop" or "break" or "dup" or "pop" or "ldnull" or "endfilter" or "endfinally" or "sizeof"
            || name.StartsWith("ldarg", StringComparison.Ordinal) || name.StartsWith("starg", StringComparison.Ordinal)
            || name.StartsWith("ldloc", StringComparison.Ordinal) || name.StartsWith("stloc", StringComparison.Ordinal)
            || name.StartsWith("ldc.", StringComparison.Ordinal)
            || name.StartsWith("conv.", StringComparison.Ordinal) && !name.Contains("ovf", StringComparison.Ordinal))
        {
            return false;
        }

        return name is not ("add" or "sub" or "mul" or "and" or "or" or "xor" or "shl" or "shr" or "shr.un"
            or "neg" or "not" or "ceq" or "cgt" or "cgt.un" or "clt" or "clt.un");
    }

    private static FilterPathState[]? ClearFilterPathStacks(FilterPathState[]? paths) => paths is null
        ? null : [.. ExpandFilterPaths(paths).Select(path => path with { Values = [] })];

    private static FilterPathState[]? UnknownFilterPathStacks(FilterPathState[]? paths) => paths is null
        ? null : [.. ExpandFilterPaths(paths).Select(path => path with { Values = [], StackUnknown = true })];

    private static FlowState<T> StartFilterPaths(FlowState<T> state) => state.Values is not { } values ? state : state with
    {
        FilterPaths =
        [
            new FilterPathState(
                [.. values.Select(value => new FilterPathValue(null, null, value.IsThis))],
                null,
                null,
                state.ThisArgumentIsOriginal),
        ],
    };

    private static FilterPathState[]? EnterExceptionRegion(FilterPathState[]? paths, FlowState<T> entry)
    {
        if (paths is null || entry.Values is not { } values)
        {
            return null;
        }

        return [.. ExpandFilterPaths(paths).Select(path => new FilterPathState(
            [.. values.Select(_ => new FilterPathValue(null, null, false))], path.Locals, path.Arguments,
            path.ThisArgumentIsOriginal, path.PendingUnwindEffect,
            ReceiverConditions: path.ReceiverConditions))];
    }

    private static FilterPathState[]? MergeFilterPaths(FilterPathState[]? left, FilterPathState[]? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        var result = new List<FilterPathState>(left);
        AddFilterPaths(result, right);

        return [.. result];
    }

    private static bool SameFilterPaths(FilterPathState[]? left, FilterPathState[]? right) =>
        left is null && right is null || left is not null && right is not null && left.Length == right.Length
            && left.All(path => right.Any(candidate => SameFilterPath(path, candidate)));

    private static bool SameFilterPath(FilterPathState left, FilterPathState right)
    {
        if (left.ThisArgumentIsOriginal != right.ThisArgumentIsOriginal || left.StackUnknown != right.StackUnknown
            || !SameFilterValues(left.Values, right.Values)
            || left.Locals?.Count != right.Locals?.Count || left.Arguments?.Count != right.Arguments?.Count
            || !SameReceiverConditions(left.ReceiverConditions, right.ReceiverConditions)
            || !SameFilterPaths(left.CorrelatedAlternatives, right.CorrelatedAlternatives)
            || !SameUnwindEffect(left.PendingUnwindEffect, right.PendingUnwindEffect))
        {
            return false;
        }

        var localsMatch = left.Locals is null || right.Locals is not null
            && left.Locals.All(pair => right.Locals.TryGetValue(pair.Key, out var value)
                && SameFilterValue(value, pair.Value));
        var argumentsMatch = left.Arguments is null || right.Arguments is not null
            && left.Arguments.All(pair => right.Arguments.TryGetValue(pair.Key, out var value)
                && SameFilterValue(value, pair.Value));
        return localsMatch && argumentsMatch;
    }

    private static bool SameReceiverConditions(IReadOnlyDictionary<int, FilterPathValue>? left,
        IReadOnlyDictionary<int, FilterPathValue>? right) => left is null && right is null
        || left is not null && right is not null && left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var condition)
                && SameFilterValue(pair.Value, condition));

    private static bool SameUnwindResult(PendingUnwindEffect? left, PendingUnwindEffect? right)
    {
        var leftBound = left is { CorrelationLost: false, BoundOutputs: not null };
        var rightBound = right is { CorrelationLost: false, BoundOutputs: not null };
        return leftBound || rightBound
            ? leftBound && rightBound && SameTransformations(left!.BoundOutputs!, right!.BoundOutputs!)
            : SameTransformations(left?.Transformations ?? IdentityReceiverTransformations,
                right?.Transformations ?? IdentityReceiverTransformations);
    }

    private static bool SameUnwindEffect(PendingUnwindEffect? left, PendingUnwindEffect? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.CorrelationLost == right.CorrelationLost
            && SameTransformations(left.Transformations, right.Transformations)
            && SameHandlerEntries(left.HandlerEntries, right.HandlerEntries)
            && (left.BoundOutputs is null && right.BoundOutputs is null
                || left.BoundOutputs is not null && right.BoundOutputs is not null
                    && SameTransformations(left.BoundOutputs, right.BoundOutputs))
            && (left.BoundHandlerEntries is null && right.BoundHandlerEntries is null
                || left.BoundHandlerEntries is not null && right.BoundHandlerEntries is not null
                    && SameHandlerEntries(left.BoundHandlerEntries, right.BoundHandlerEntries));
    }

    private static bool SameHandlerEntries(IReadOnlyDictionary<int, FilterPathState[]> left,
        IReadOnlyDictionary<int, FilterPathState[]> right) => left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var entries)
            && SameTransformations(pair.Value, entries));

    private static bool SameTransformations(FilterPathState[] left, FilterPathState[] right) =>
        left.Length == right.Length && left.All(path => right.Any(candidate => SameFilterPath(path, candidate)));

    private static bool SameFilterValues(FilterPathValue[] left, FilterPathValue[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair => SameFilterValue(pair.First, pair.Second));

    private static bool SameFilterValue(FilterPathValue left, FilterPathValue right) =>
        left.IsZero == right.IsZero && left.IsOne == right.IsOne && left.IsThis == right.IsThis
        && left.HasNonSourceAlternative == right.HasNonSourceAlternative
        && left.IntegerValue == right.IntegerValue
        && left.ComparedSource == right.ComparedSource && left.ComparedInteger == right.ComparedInteger
        && left.IsNull == right.IsNull && left.ComparedOtherSource == right.ComparedOtherSource
        && left.ComparedWithNull == right.ComparedWithNull
        && left.MayBeNaN == right.MayBeNaN
        && left.IsNaN == right.IsNaN
        && ReceiverSourcesOf(left).SequenceEqual(ReceiverSourcesOf(right))
        && SameSet(left.ExcludedIntegers, right.ExcludedIntegers)
        && SameSet(left.EqualSources, right.EqualSources)
        && SameSet(left.ExcludedSources, right.ExcludedSources);

    private static bool SameSet<TValue>(IReadOnlyList<TValue>? left, IReadOnlyList<TValue>? right) =>
        left is null && right is null || left is not null && right is not null
            && left.Count == right.Count && left.SequenceEqual(right);

    private static FilterPathValue ConstantFilterValue(StackOperandView<T> view, bool isThis)
    {
        if (view.Op == OpCodes.Ldc_I4_0)
        {
            return new FilterPathValue(true, false, isThis, IntegerValue: 0);
        }

        if (view.Op == OpCodes.Ldc_I4_1)
        {
            return new FilterPathValue(false, true, isThis, IntegerValue: 1);
        }

        if (view.Op == OpCodes.Ldc_I4_M1 || view.Op == OpCodes.Ldc_I4_2
            || view.Op == OpCodes.Ldc_I4_3 || view.Op == OpCodes.Ldc_I4_4 || view.Op == OpCodes.Ldc_I4_5
            || view.Op == OpCodes.Ldc_I4_6 || view.Op == OpCodes.Ldc_I4_7 || view.Op == OpCodes.Ldc_I4_8)
        {
            var value = view.Op == OpCodes.Ldc_I4_M1 ? -1 : view.Op.Value - OpCodes.Ldc_I4_0.Value;
            return new FilterPathValue(false, false, isThis, IntegerValue: value);
        }

        if (view.Op == OpCodes.Ldnull)
        {
            return new FilterPathValue(null, null, isThis, IsNull: true);
        }

        if (view.Op != OpCodes.Ldc_I4 && view.Op != OpCodes.Ldc_I4_S && view.Op != OpCodes.Ldc_I8)
        {
            return new FilterPathValue(null, null, isThis);
        }

        return view.IntegerOperand switch
        {
            0 => new FilterPathValue(true, false, isThis, IntegerValue: 0),
            1 => new FilterPathValue(false, true, isThis, IntegerValue: 1),
            { } value => new FilterPathValue(false, false, isThis, IntegerValue: value),
            _ => new FilterPathValue(null, null, isThis),
        };
    }

    private List<AnalysisRelatedLocation> Related(IReadOnlyList<FlowNode<T>> nodes, int predecessor, FlowState<T> state)
    {
        var result = new List<AnalysisRelatedLocation>();
        if (predecessor >= 0 && predecessor < nodes.Count)
        {
            result.Add(new AnalysisRelatedLocation(nodes[predecessor].Location, $"incoming stack {_types.Render(state)}"));
        }

        foreach (var value in state.Values ?? [])
        {
            foreach (var origin in value.Origins)
            {
                result.Add(new AnalysisRelatedLocation(nodes[origin].Location, $"produces {_types.Name(value.Type)}"));
            }
        }

        return result;
    }

    private static string Path(IReadOnlyList<FlowNode<T>> nodes, int index) => index < 0 ? "the entry"
        : nodes[index].Location.Offset is { } offset ? $"IL_{offset:X4}"
        : $"line {nodes[index].Location.Line + 1}";
}
