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
        var nodes = graph.Nodes;
        graph.ValidateRegions();
        var before = new FlowState<T>?[nodes.Count + 1];
        var after = new FlowState<T>?[nodes.Count + 1];
        var incoming = Enumerable.Range(0, before.Length).Select(_ => new Dictionary<int, FlowState<T>>()).ToArray();
        var diagnostics = new Dictionary<(int, string), AnalysisDiagnostic>();
        var queue = new Queue<int>();
        var queued = new bool[before.Length];
        var maxStack = 0;

        void Enqueue(int position)
        {
            if (!queued[position])
            {
                queued[position] = true;
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

        void Propagate(int target, int predecessor, FlowState<T> state)
        {
            if (!incoming[target].TryGetValue(predecessor, out var old) || !Equal(old, state))
            {
                incoming[target][predecessor] = state;
                Enqueue(target);
            }
        }

        foreach (var (position, seed) in graph.Seeds)
        {
            Propagate(position, -1, seed);
        }

        var iterations = 0;
        while (queue.TryDequeue(out var index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations % 64 == 0)
            {
                yield return null;
            }
            queued[index] = false;
            FlowState<T>? state = null;
            var first = -1;
            var predecessors = (IEnumerable<KeyValuePair<int, FlowState<T>>>)incoming[index];
            if (incoming[index].Count > 1)
            {
                predecessors = incoming[index].OrderBy(pair => pair.Key);
            }

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

            before[index] = state;
            if (state is null)
            {
                continue;
            }

            maxStack = Math.Max(maxStack, state.Values?.Length ?? 0);
            if (index == nodes.Count)
            {
                after[index] = state;
                continue;
            }

            var node = nodes[index];
            if (node.EffectUnknown)
            {
                Report(index, "FLOW004", "the instruction's stack effect is unknown", AnalysisDiagnosticKind.Unknown);
                state = state.Invalid ? state : FlowState<T>.Unknown;
            }
            else if (node.Instruction is { } view && !state.Invalid && state.Values is { } values)
            {
                var problem = Validate(view, values, graph, index, returnType, cell);
                if (problem is not null)
                {
                    Report(index, "FLOW005", problem, related: Related(nodes, index, state));
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
                        foreach (var type in pushed)
                        {
                            output.Add(view.Op == OpCodes.Dup ? popped[0]
                                : new FlowValue<T>(type, [index], view.LoadsThis || view.AddressOfThis,
                                    graph.Prefixes(index).Any(prefix => prefix.Op == OpCodes.Readonly)
                                        || view.Op == OpCodes.Unbox
                                        || view.Op == OpCodes.Ldflda && popped.Any(value => value.IsReadOnly)));
                        }

                        if (StackTransfer<T>.EndsPath(view.Op))
                        {
                            output.Clear();
                        }

                        state = new FlowState<T>([.. output], state.HasUnknownPath);
                    }
                }

                if (view.Op.Name is "localloc" or "cpblk" or "initblk" or "calli" or "jmp"
                    || view.Op == OpCodes.Mkrefany && values.LastOrDefault()?.Type is { } typedReference
                        && _types.Category(typedReference) == StackCategory.NativeInt
                    || values.Any(value => value.Type is { } type && _types.Algebra.IsPointer(type))
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
                    || view.Op == OpCodes.Ldvirtftn && view.MethodIsConstructor == true)
                {
                    Report(index, "FLOW007", $"{view.Op.Name} uses an operation outside verifiable IL",
                        AnalysisDiagnosticKind.Unverifiable);
                }
            }

            after[index] = state;
            maxStack = Math.Max(maxStack, state.Values?.Length ?? 0);
            foreach (var edge in graph.Edges[index])
            {
                var outgoing = edge.ClearsStack && !state.Invalid ? FlowState<T>.Empty : state;
                Propagate(edge.Target, index, outgoing);
            }
        }

        graph.ValidateStacks(before);
        yield return new FlowResult<T>(before, after,
            [.. graph.Diagnostics,
                .. diagnostics.OrderBy(pair => pair.Key.Item1).ThenBy(pair => pair.Key.Item2).Select(pair => pair.Value)],
            maxStack);
    }

    private string? Validate(StackOperandView<T> view, FlowValue<T>[] values, FlowGraph<T> graph, int index, T? returnType, bool cell)
    {
        var op = view.Op;
        var count = values.Length;
        var top = count > 0 ? values[^1].Type : null;
        var kind = top is null ? (StackCategory?)null : _types.Category(top);
        if (op == OpCodes.Ret)
        {
            if (graph.Regions[index].Length > 0)
            {
                return "ret is not allowed inside a protected region; use leave to exit it first";
            }

            if (cell)
            {
                if (count > 1)
                {
                    return $"the stack must hold 0 or 1 value at ret, but has {count}: {_types.Render(new FlowState<T>(values))}";
                }

                return top is not null && (_types.Algebra.IsByRef(top) || _types.Algebra.IsPointer(top))
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

        if (op == OpCodes.Newobj && (view.MethodIsStatic == true || view.MethodIsConstructor == false))
        {
            return "newobj needs an instance constructor";
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
                if (_types.Algebra.IsByRef(type) || _types.Algebra.IsPointer(type))
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

            if (_types.Algebra.IsPointer(type))
            {
                return _types.CanAssign(_types.Algebra.ElementOf(type), owner);
            }

            if (_types.Category(type) == StackCategory.NativeInt)
            {
                return true;
            }

            if (_types.Algebra.IsByRef(type))
            {
                return _types.Algebra.Same(_types.Algebra.ElementOf(type), owner);
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

        if (op == OpCodes.Unbox && view.Type is { } unboxed && !_types.Algebra.IsValueType(unboxed)
            && !_types.Algebra.IsGenericParameter(unboxed))
        {
            return $"unbox needs a value type or generic parameter but found {_types.Name(unboxed)}";
        }

        if (op == OpCodes.Jmp && count != 0)
        {
            return "jmp requires an empty evaluation stack";
        }

        if (op == OpCodes.Jmp && view.JumpRestriction is { } jumpRestriction)
        {
            return jumpRestriction;
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

        if (view.StoreRestriction is { } storeRestriction)
        {
            return storeRestriction;
        }

        if (view.ReceiverRestriction is { } receiverRestriction && !values[count - pops].IsThis)
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
            if (_types.Algebra.IsByRef(pointer) || _types.Algebra.IsPointer(pointer))
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
            var storage = StorageType(view);
            if (memory == "cpobj" && top is { } source && _types.Algebra.IsByRef(source) && storage is not null
                && _types.Algebra.ElementOf(source) is { } sourceType && !_types.CanAssign(sourceType, storage))
            {
                return $"cpobj cannot copy {_types.Name(sourceType)} as {_types.Name(storage)}";
            }

            if (address is not null && _types.Algebra.IsByRef(address) && storage is not null
                && _types.Algebra.ElementOf(address) is { } element
                && !(memory is "ldind.ref" or "stind.ref"
                    ? Reference(element)
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
                && arrayOp is not ("ldelem.ref" or "stelem.ref")
                && !(arrayOp.StartsWith("stelem", StringComparison.Ordinal)
                    ? _types.ArrayElementCompatible(instructionElement, actualElement)
                    : _types.ArrayElementCompatible(actualElement, instructionElement)))
            {
                return $"{arrayOp} cannot access {_types.Name(actualElement)} elements as {_types.Name(instructionElement)}";
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

    private T? StorageType(StackOperandView<T> view)
    {
        if (view.Type is { } type)
        {
            return type;
        }

        var keyword = view.Op.Name?.Split('.').Last() switch
        {
            "i1" or "u1" or "i2" or "u2" or "i4" or "u4" => "int32",
            "i8" => "int64",
            "i" => "native int",
            "r4" or "r8" => "float64",
            "ref" => "object",
            _ => null,
        };
        return keyword is null ? null : _types.Algebra.Primitive(keyword);
    }

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
            merged = new FlowState<T>(null, Invalid: true);
            return true;
        }

        var unknown = left.HasUnknownPath || right.HasUnknownPath;
        if (left.Values is not { } a || right.Values is not { } b)
        {
            merged = new FlowState<T>(left.Values ?? right.Values, unknown);
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
                a[i].IsThis && b[i].IsThis, a[i].IsReadOnly || b[i].IsReadOnly);
        }

        merged = new FlowState<T>(values, unknown);
        return true;
    }

    private bool Equal(FlowState<T> left, FlowState<T> right)
    {
        if (left.Invalid != right.Invalid || left.HasUnknownPath != right.HasUnknownPath || left.Values?.Length != right.Values?.Length)
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
