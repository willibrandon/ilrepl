using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilInstruction = Mono.Cecil.Cil.Instruction;
using CecilOpCode = Mono.Cecil.Cil.OpCode;

namespace IlRepl.Engine;

/// <summary>
/// Emits a validated body into a Cecil method: the same entries the <c>ILGenerator</c> path
/// emits for a cell, with the structured exception blocks turned into handler ranges and the
/// <c>leave</c>, <c>endfinally</c>, and <c>endfilter</c> instructions that <c>ILGenerator</c>
/// inserts at block boundaries written out explicitly.
/// </summary>
public static class CecilBodyEmitter
{
    /// <summary>
    /// Emits the body.
    /// </summary>
    /// <param name="method">The method to fill; its parameters must already be defined.</param>
    /// <param name="state">The validated body.</param>
    /// <param name="writer">The writer that imports references.</param>
    /// <param name="map">The identity map for operands.</param>
    public static void Emit(MethodDefinition method, CellState state, CecilWriter writer, EmitMap map)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(map);
        state.RequireValidFlow();
        new Emitter(method, state, writer, map).Run();
        writer.SetStackLimit(method, Math.Max(1, state.Analysis.MaxStack));
    }

    private sealed class Emitter(MethodDefinition method, CellState state, CecilWriter writer, EmitMap map)
    {
        private static readonly Dictionary<string, CecilOpCode> OpCodesByName = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(CecilOpCode))
            .Select(f => (CecilOpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Name, StringComparer.Ordinal);

        private readonly ILProcessor _il = method.Body.GetILProcessor();
        private readonly List<VariableDefinition> _locals = [];
        private readonly Dictionary<string, Marker> _labels = new(StringComparer.Ordinal);
        private readonly List<Marker> _pending = [];
        private readonly List<(CecilInstruction Instruction, Marker Target)> _branches = [];
        private readonly List<(CecilInstruction Instruction, Marker[] Targets)> _switches = [];
        private readonly List<Frame> _frames = [];

        public void Run()
        {
            method.Body.InitLocals = true;
            method.Body.MaxStackSize = Math.Max(1, state.Analysis.MaxStack);
            foreach (var local in state.Locals)
            {
                var type = writer.Import(map.Map(local.Type));
                var variable = new VariableDefinition(local.IsPinned ? new PinnedType(type) : type);
                method.Body.Variables.Add(variable);
                _locals.Add(variable);
            }

            foreach (var label in state.DefinedLabels)
            {
                _labels[label] = new Marker();
            }

            foreach (var entry in state.Entries)
            {
                foreach (var label in entry.Labels)
                {
                    _pending.Add(_labels[label]);
                }

                switch (entry.Kind)
                {
                    case EntryKind.Instruction:
                        EmitInstruction(entry.Instruction!);
                        break;
                    case EntryKind.Block:
                        EmitBlock(entry);
                        break;
                    default:
                        break;
                }
            }

            EmitTail();
            Resolve();
        }

        private void EmitTail()
        {
            if (state.LastInstructionEndsFlow)
            {
                if (_pending.Count > 0)
                {
                    Append(_il.Create(OpCodes.Ldnull));
                    Append(_il.Create(OpCodes.Throw));
                }

                return;
            }

            if (state.IsMethod)
            {
                Append(_il.Create(OpCodes.Ret));
                return;
            }

            var top = state.Stack.Top;
            if (state.Stack.Count == 0)
            {
                Append(_il.Create(OpCodes.Ldnull));
            }
            else if (top is not null && top != typeof(NullReferenceMarker) && top.IsValueType)
            {
                Append(_il.Create(OpCodes.Box, writer.Import(map.Map(top))));
            }

            Append(_il.Create(OpCodes.Ret));
        }

        private void EmitInstruction(Instruction instruction)
        {
            var op = Translate(instruction.Op);
            if (instruction.Op == System.Reflection.Emit.OpCodes.Ret)
            {
                if (instruction.RetNull)
                {
                    Append(_il.Create(OpCodes.Ldnull));
                }
                else if (instruction.RetBox is not null)
                {
                    Append(_il.Create(OpCodes.Box, writer.Import(map.Map(instruction.RetBox))));
                }

                Append(_il.Create(OpCodes.Ret));
                return;
            }

            switch (instruction.Kind)
            {
                case OperandKind.None:
                    Append(_il.Create(op));
                    break;
                case OperandKind.SByte:
                    Append(_il.Create(op, (sbyte)instruction.Operand!));
                    break;
                case OperandKind.Byte:
                    Append(_il.Create(op, (byte)instruction.Operand!));
                    break;
                case OperandKind.Int32:
                    Append(_il.Create(op, (int)instruction.Operand!));
                    break;
                case OperandKind.Int64:
                    Append(_il.Create(op, (long)instruction.Operand!));
                    break;
                case OperandKind.Single:
                    Append(_il.Create(op, (float)instruction.Operand!));
                    break;
                case OperandKind.Double:
                    Append(_il.Create(op, (double)instruction.Operand!));
                    break;
                case OperandKind.String:
                    Append(_il.Create(op, (string)instruction.Operand!));
                    break;
                case OperandKind.Label:
                {
                    var branch = _il.Create(op, _il.Create(OpCodes.Nop));
                    _branches.Add((branch, _labels[(string)instruction.Operand!]));
                    Append(branch);
                    break;
                }

                case OperandKind.Labels:
                {
                    var targets = ((string[])instruction.Operand!).Select(l => _labels[l]).ToArray();
                    var branch = _il.Create(op, Array.Empty<CecilInstruction>());
                    _switches.Add((branch, targets));
                    Append(branch);
                    break;
                }

                case OperandKind.Local:
                    Append(_il.Create(op, _locals[(int)instruction.Operand!]));
                    break;
                case OperandKind.Argument:
                    Append(_il.Create(op, Parameter((int)instruction.Operand!)));
                    break;
                case OperandKind.Type:
                    Append(_il.Create(op, writer.Import(map.Map((Type)instruction.Operand!))));
                    break;
                case OperandKind.Field:
                    Append(_il.Create(op, writer.Import(map.Map((FieldInfo)instruction.Operand!))));
                    break;
                case OperandKind.Method:
                    Append(_il.Create(op, MethodOperand((ResolvedMethod)instruction.Operand!, callSite: op.Code is Code.Call or Code.Callvirt)));
                    break;
                case OperandKind.Token:
                    Append(instruction.Operand switch
                    {
                        Type t => _il.Create(op, writer.Import(map.Map(t))),
                        FieldInfo f => _il.Create(op, writer.Import(map.Map(f))),
                        ResolvedMethod r => _il.Create(op, MethodOperand(r, callSite: false)),
                        _ => throw new ReplException("unsupported token operand"),
                    });
                    break;
                case OperandKind.Signature:
                    Append(_il.Create(op, CallSite((CalliSignature)instruction.Operand!)));
                    break;
                default:
                    throw new ReplException($"unsupported operand kind {instruction.Kind}");
            }
        }

        private MethodReference MethodOperand(ResolvedMethod resolved, bool callSite)
        {
            var target = resolved.Definition is { } definition ? map.SessionMethod(definition) : map.Map(resolved.Method!);
            var reference = resolved.Definition is null && resolved.Declared is not null ? writer.Import(target, resolved.DeclaringType) : writer.Import(target);
            if (resolved.GenericArguments is { Count: > 0 } arguments && reference is not GenericInstanceMethod)
            {
                var instance = new GenericInstanceMethod(reference);
                foreach (var argument in arguments)
                {
                    instance.GenericArguments.Add(writer.Import(map.Map(argument)));
                }

                reference = instance;
            }

            if (!callSite || resolved.OptionalParameterTypes is not { Length: > 0 } optional)
            {
                return reference;
            }

            // A vararg call site names the extra parameters after a sentinel.
            var site = new MethodReference(reference.Name, reference.ReturnType, reference.DeclaringType)
            {
                HasThis = reference.HasThis,
                ExplicitThis = reference.ExplicitThis,
                CallingConvention = MethodCallingConvention.VarArg,
            };
            foreach (var parameter in reference.Parameters)
            {
                site.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            }

            for (var i = 0; i < optional.Length; i++)
            {
                var type = writer.Import(map.Map(optional[i]));
                site.Parameters.Add(new ParameterDefinition(i == 0 ? new SentinelType(type) : type));
            }

            return site;
        }

        private CallSite CallSite(CalliSignature signature)
        {
            var site = new CallSite(writer.Import(map.Map(signature.ReturnType)));
            if (signature.IsUnmanaged)
            {
                site.CallingConvention = signature.UnmanagedConvention switch
                {
                    System.Runtime.InteropServices.CallingConvention.Cdecl => MethodCallingConvention.C,
                    System.Runtime.InteropServices.CallingConvention.StdCall => MethodCallingConvention.StdCall,
                    System.Runtime.InteropServices.CallingConvention.ThisCall => MethodCallingConvention.ThisCall,
                    System.Runtime.InteropServices.CallingConvention.FastCall => MethodCallingConvention.FastCall,
                    _ => MethodCallingConvention.Unmanaged,
                };
            }
            else
            {
                site.HasThis = signature.ManagedConvention.HasFlag(CallingConventions.HasThis);
                site.ExplicitThis = signature.ManagedConvention.HasFlag(CallingConventions.ExplicitThis);
                site.CallingConvention = signature.ManagedConvention.HasFlag(CallingConventions.VarArgs) ? MethodCallingConvention.VarArg : MethodCallingConvention.Default;
            }

            foreach (var type in signature.ParameterTypes)
            {
                site.Parameters.Add(new ParameterDefinition(writer.Import(map.Map(type))));
            }

            if (signature.OptionalParameterTypes is { } optional)
            {
                for (var i = 0; i < optional.Length; i++)
                {
                    var type = writer.Import(map.Map(optional[i]));
                    site.Parameters.Add(new ParameterDefinition(i == 0 ? new SentinelType(type) : type));
                }
            }

            return site;
        }

        private ParameterDefinition Parameter(int index)
        {
            if (method.HasThis)
            {
                return index == 0 ? method.Body.ThisParameter : method.Parameters[index - 1];
            }

            return method.Parameters[index];
        }

        private void EmitBlock(CellEntry entry)
        {
            switch (entry.Block)
            {
                case BlockKind.Try:
                    _frames.Add(new Frame { TryStart = Mark() });
                    break;
                case BlockKind.Catch:
                    LeaveCurrent();
                    _frames[^1].Handlers.Add(new Handler { Kind = BlockKind.Catch, CatchType = writer.Import(map.Map(entry.CatchType ?? typeof(object))), Start = Mark() });
                    break;
                case BlockKind.Filter:
                    LeaveCurrent();
                    _frames[^1].Handlers.Add(new Handler { Kind = BlockKind.Filter, FilterStart = Mark() });
                    break;
                case BlockKind.FilterHandler:
                    // The filter's own endfilter was emitted as an instruction; the handler starts here.
                    _frames[^1].Handlers[^1].Start = Mark();
                    break;
                case BlockKind.Finally:
                    LeaveCurrent();
                    _frames[^1].Handlers.Add(new Handler { Kind = BlockKind.Finally, Start = Mark() });
                    break;
                case BlockKind.Fault:
                    LeaveCurrent();
                    _frames[^1].Handlers.Add(new Handler { Kind = BlockKind.Fault, Start = Mark() });
                    break;
                case BlockKind.End:
                {
                    var frame = _frames[^1];
                    _frames.RemoveAt(_frames.Count - 1);
                    var last = frame.Handlers[^1];
                    Append(last.Kind is BlockKind.Finally or BlockKind.Fault ? _il.Create(OpCodes.Endfinally) : Leave(frame.End));
                    _pending.Add(frame.End);
                    frame.Complete = true;
                    _completed.Add(frame);
                    break;
                }

                default:
                    break;
            }
        }

        private readonly List<Frame> _completed = [];

        private void LeaveCurrent()
        {
            var frame = _frames[^1];
            if (frame.Handlers.Count == 0 || frame.Handlers[^1].Kind is BlockKind.Catch or BlockKind.Filter)
            {
                // Leaving the try block, or a catch or filter handler, needs an explicit leave;
                // ILGenerator writes the same one when the next handler begins.
                Append(Leave(frame.End));
            }
        }

        private CecilInstruction Leave(Marker target)
        {
            var leave = _il.Create(OpCodes.Leave, _il.Create(OpCodes.Nop));
            _branches.Add((leave, target));
            return leave;
        }

        private Marker Mark()
        {
            var marker = new Marker();
            _pending.Add(marker);
            return marker;
        }

        private void Append(CecilInstruction instruction)
        {
            foreach (var marker in _pending)
            {
                marker.Target = instruction;
            }

            _pending.Clear();
            _il.Append(instruction);
        }

        private void Resolve()
        {
            if (_pending.Count > 0)
            {
                // Every body ends with ret, so a marker after the last instruction cannot happen.
                throw new InvalidOperationException("a label or block boundary has no instruction after it");
            }

            foreach (var (instruction, target) in _branches)
            {
                instruction.Operand = target.Target!;
            }

            foreach (var (instruction, targets) in _switches)
            {
                instruction.Operand = targets.Select(t => t.Target!).ToArray();
            }

            foreach (var frame in _completed)
            {
                var tryEnd = frame.Handlers[0].FilterStart?.Target ?? frame.Handlers[0].Start!.Target!;
                for (var i = 0; i < frame.Handlers.Count; i++)
                {
                    var handler = frame.Handlers[i];
                    var next = i + 1 < frame.Handlers.Count ? frame.Handlers[i + 1] : null;
                    var handlerEnd = next is null ? frame.End.Target! : next.FilterStart?.Target ?? next.Start!.Target!;
                    var terminal = handler.Kind is BlockKind.Finally or BlockKind.Fault;
                    var cecil = new ExceptionHandler(handler.Kind switch
                    {
                        BlockKind.Catch => ExceptionHandlerType.Catch,
                        BlockKind.Filter => ExceptionHandlerType.Filter,
                        BlockKind.Finally => ExceptionHandlerType.Finally,
                        _ => ExceptionHandlerType.Fault,
                    })
                    {
                        TryStart = frame.TryStart.Target!,
                        // A finally or fault written after catch handlers protects the try block
                        // and those handlers together, the way ILGenerator nests them: its
                        // region ends where the handler itself begins.
                        TryEnd = terminal && i > 0 ? handler.Start!.Target! : tryEnd,
                        HandlerStart = handler.Start!.Target!,
                        HandlerEnd = handlerEnd,
                        CatchType = handler.CatchType,
                        FilterStart = handler.FilterStart?.Target,
                    };
                    method.Body.ExceptionHandlers.Add(cecil);
                }
            }
        }

        private static CecilOpCode Translate(System.Reflection.Emit.OpCode op)
        {
            if (op == System.Reflection.Emit.OpCodes.Ldelem)
            {
                return OpCodes.Ldelem_Any;
            }

            if (op == System.Reflection.Emit.OpCodes.Stelem)
            {
                return OpCodes.Stelem_Any;
            }

            return OpCodesByName.TryGetValue(op.Name!, out var cecil) ? cecil
                : throw new ReplException($"opcode '{op.Name}' cannot be written by the exporter");
        }

        private sealed class Marker
        {
            public CecilInstruction? Target { get; set; }
        }

        private sealed class Handler
        {
            public required BlockKind Kind { get; init; }
            public Marker? Start { get; set; }
            public Marker? FilterStart { get; init; }
            public TypeReference? CatchType { get; init; }
        }

        private sealed class Frame
        {
            public required Marker TryStart { get; init; }
            public Marker End { get; } = new();
            public List<Handler> Handlers { get; } = [];
            public bool Complete { get; set; }
        }
    }
}
