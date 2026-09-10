using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Computes instruction stack effects using shared rules for runtime types and preview symbols.
/// </summary>
/// <remarks>
/// The stack effect of one instruction: how many values it pops, computed from the opcode and its
/// operand, and what it pushes, computed from the opcode, the operand, and what was popped. One
/// set of rules serves the runtime stack model and the completer's preview of a candidate.
/// </remarks>
/// <typeparam name="T">The type representation.</typeparam>
public sealed class StackTransfer<T> where T : class
{
    private readonly IStackTypeAlgebra<T> _types;

    /// <summary>
    /// Initializes the transfer over a type algebra.
    /// </summary>
    /// <param name="types">The algebra.</param>
    public StackTransfer(IStackTypeAlgebra<T> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = types;
    }

    /// <summary>
    /// How many values the instruction pops.
    /// </summary>
    /// <param name="view">The instruction.</param>
    /// <returns>The pop count.</returns>
    public static int PopCount(StackOperandView<T> view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var op = view.Op;
        return op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
                or StackBehaviour.Popref_popi_pop1 => 3,
            StackBehaviour.Varpop => op == OpCodes.Ret ? view.RetPops : view.ArgumentPops,
            _ => 0,
        };
    }

    /// <summary>
    /// True when nothing after the instruction is reachable on the same path, so the stack is cleared.
    /// </summary>
    /// <param name="op">The opcode.</param>
    /// <returns>True when the instruction ends the path.</returns>
    public static bool EndsPath(OpCode op) =>
        op.FlowControl is FlowControl.Throw || op == OpCodes.Leave || op == OpCodes.Leave_S || op == OpCodes.Endfinally
        || op == OpCodes.Endfilter || op == OpCodes.Jmp;

    /// <summary>
    /// The types the instruction pushes, bottom first. A null entry is a slot of unknown type.
    /// </summary>
    /// <param name="view">The instruction.</param>
    /// <param name="popped">The values popped, bottom first.</param>
    /// <returns>The pushed types.</returns>
    public IReadOnlyList<T?> PushTypes(StackOperandView<T> view, IReadOnlyList<T?> popped)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(popped);
        var op = view.Op;
        var name = op.Name ?? "";

        switch (name)
        {
            case "dup":
                return [popped[0], popped[0]];
            case "ldstr":
                return [_types.Primitive("string")];
            case "ldnull":
                return [_types.NullReference];
            case "ldtoken":
                return [view.Token switch
                {
                    StackTokenKind.Type => _types.CoreLib("System.RuntimeTypeHandle"),
                    StackTokenKind.Field => _types.CoreLib("System.RuntimeFieldHandle"),
                    _ => _types.CoreLib("System.RuntimeMethodHandle"),
                }];
            case "ldftn":
            case "ldvirtftn":
            case "ldlen":
            case "localloc":
                return [_types.Primitive("native int")];
            case "sizeof":
                return [_types.Primitive("uint32")];
            case "arglist":
                return [_types.CoreLib("System.RuntimeArgumentHandle")];
            case "mkrefany":
                return [_types.Primitive("typedref")];
            case "refanytype":
                return [_types.CoreLib("System.RuntimeTypeHandle")];
            case "box":
                return [Box(view.Type!)];
            case "newarr":
                return [_types.MakeArray(view.Type!)];
            case "castclass":
            case "isinst":
            case "unbox.any":
            case "ldobj":
            case "ldelem":
                return [view.Type];
            case "unbox":
            case "ldelema":
            case "refanyval":
                return [_types.MakeByRef(view.Type!)];
            case "newobj":
                return [view.DeclaringType];
            case "ldfld":
            case "ldsfld":
                return [view.FieldType];
            case "ldflda":
            case "ldsflda":
                return [_types.MakeByRef(view.FieldType!)];
            case "ldloc":
            case "ldloc.s":
            case "ldloc.0":
            case "ldloc.1":
            case "ldloc.2":
            case "ldloc.3":
            case "ldarg":
            case "ldarg.s":
            case "ldarg.0":
            case "ldarg.1":
            case "ldarg.2":
            case "ldarg.3":
                return [view.SlotType];
            case "ldloca":
            case "ldloca.s":
            case "ldarga":
            case "ldarga.s":
                return [_types.MakeByRef(view.SlotType!)];
            case "call":
            case "callvirt":
            case "calli":
                return view.ReturnType is null ? [] : [view.ReturnType];
            case "add":
            case "sub":
            case "mul":
            case "div":
            case "div.un":
            case "rem":
            case "rem.un":
            case "and":
            case "or":
            case "xor":
            case "add.ovf":
            case "add.ovf.un":
            case "sub.ovf":
            case "sub.ovf.un":
            case "mul.ovf":
            case "mul.ovf.un":
                return [Binary(popped[0], popped[1])];
            case "shl":
            case "shr":
            case "shr.un":
            case "neg":
            case "not":
                return [popped[0]];
            case "ldind.ref":
                return [popped.Count > 0 && popped[0] is { } indirect && (_types.IsByRef(indirect) || _types.IsPointer(
                    indirect)) ? _types.ElementOf(indirect) : _types.UnknownReference];
            case "ldelem.ref":
                return [popped.Count > 1 && popped[0] is { } array && _types.IsArray(array) ? _types.ElementOf(
                    array) : _types.UnknownReference];
            default:
                break;
        }

        if (name.StartsWith("conv.", StringComparison.Ordinal))
        {
            var suffix = name["conv.".Length..].Replace("ovf.", "", StringComparison.Ordinal);
            if (suffix.EndsWith(".un", StringComparison.Ordinal))
            {
                suffix = suffix[..^3];
            }

            return [NumericSuffix(suffix)];
        }

        if (name.StartsWith("ldelem.", StringComparison.Ordinal) || name.StartsWith("ldind.", StringComparison.Ordinal))
        {
            return [NumericSuffix(name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..])];
        }

        return op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => [],
            StackBehaviour.Pushi => [_types.Primitive("int32")],
            StackBehaviour.Pushi8 => [_types.Primitive("int64")],
            StackBehaviour.Pushr4 => [_types.Primitive("float32")],
            StackBehaviour.Pushr8 => [_types.Primitive("float64")],
            StackBehaviour.Pushref => [_types.UnknownReference],
            StackBehaviour.Push1 => [null],
            StackBehaviour.Push1_push1 => [null, null],
            _ => [],
        };
    }

    /// <summary>
    /// Computes the stack entry produced by boxing while preserving known value-type information.
    /// </summary>
    /// <remarks>
    /// The entry <c>box</c> pushes: boxing a reference type is the identity, a generic parameter
    /// could be either, and only a known value type becomes a boxed entry that remembers what it holds.
    /// </remarks>
    /// <param name="operand">The boxed type.</param>
    /// <returns>The stack entry.</returns>
    public T Box(T operand)
    {
        ArgumentNullException.ThrowIfNull(operand);
        if (_types.IsGenericParameter(operand))
        {
            return _types.UnknownReference;
        }

        if (!_types.IsValueType(operand))
        {
            return operand;
        }

        return _types.Boxed(_types.NullableUnderlying(operand) ?? operand);
    }

    private T? NumericSuffix(string suffix) => suffix switch
    {
        "i1" => _types.Primitive("int8"),
        "u1" => _types.Primitive("uint8"),
        "i2" => _types.Primitive("int16"),
        "u2" => _types.Primitive("uint16"),
        "i4" => _types.Primitive("int32"),
        "u4" => _types.Primitive("uint32"),
        "i8" => _types.Primitive("int64"),
        "u8" => _types.Primitive("uint64"),
        "r4" => _types.Primitive("float32"),
        "r8" or "r" => _types.Primitive("float64"),
        "i" => _types.Primitive("native int"),
        "u" => _types.Primitive("native uint"),
        _ => null,
    };

    private T? Binary(T? a, T? b)
    {
        var float64 = _types.Primitive("float64");
        if (_types.Same(a, float64) || _types.Same(b, float64))
        {
            return float64;
        }

        var float32 = _types.Primitive("float32");
        if (_types.Same(a, float32) || _types.Same(b, float32))
        {
            return float32;
        }

        var int64 = _types.Primitive("int64");
        if (_types.Same(a, int64) || _types.Same(b, int64))
        {
            return int64;
        }

        var uint64 = _types.Primitive("uint64");
        if (_types.Same(a, uint64) || _types.Same(b, uint64))
        {
            return uint64;
        }

        if (a is not null && _types.IsByRef(a))
        {
            return a;
        }

        if (b is not null && _types.IsByRef(b))
        {
            return b;
        }

        if (a is not null && _types.IsPointer(a))
        {
            return a;
        }

        if (b is not null && _types.IsPointer(b))
        {
            return b;
        }

        var nativeInt = _types.Primitive("native int");
        if (_types.Same(a, nativeInt) || _types.Same(b, nativeInt))
        {
            return nativeInt;
        }

        var nativeUInt = _types.Primitive("native uint");
        if (_types.Same(a, nativeUInt) || _types.Same(b, nativeUInt))
        {
            return nativeUInt;
        }

        if (a is null || b is null)
        {
            return null;
        }

        return _types.Primitive("int32");
    }
}
