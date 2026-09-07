using System.Reflection;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// A static, linear model of the evaluation stack with best-effort types. It runs before an
/// instruction is accepted so underflows and arity mistakes are reported at the prompt, and it
/// is what the stack echo after each line shows.
/// </summary>
public sealed class StackSimulator
{
    private readonly List<Type?> _items = [];
    private readonly List<bool> _isThis = [];

    /// <summary>
    /// The entries from bottom to top. A null entry has an unknown type.
    /// </summary>
    public IReadOnlyList<Type?> Items => _items;

    /// <summary>
    /// The number of entries.
    /// </summary>
    public int Count => _items.Count;

    /// <summary>
    /// The top entry, or null when the stack is empty.
    /// </summary>
    public Type? Top => _items.Count == 0 ? null : _items[^1];

    /// <summary>
    /// A stack holding the given entries, bottom first; a null entry is a slot of unknown type.
    /// </summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The stack.</returns>
    public static StackSimulator WithEntries(params Type?[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var simulator = new StackSimulator();
        foreach (var entry in entries)
        {
            simulator.Push(entry, false);
        }

        return simulator;
    }

    /// <summary>
    /// Copies the stack.
    /// </summary>
    /// <returns>An independent copy.</returns>
    public StackSimulator Clone()
    {
        var copy = new StackSimulator();
        copy._items.AddRange(_items);
        copy._isThis.AddRange(_isThis);
        return copy;
    }

    /// <summary>
    /// Empties the stack.
    /// </summary>
    public void Clear()
    {
        _items.Clear();
        _isThis.Clear();
    }

    /// <summary>
    /// Merges another stack of the same depth into this one, slot by slot, the way the CLI merges
    /// stack states at a join (ECMA-335 III.1.8.1.3): by stack category, with an unknown slot
    /// staying unknown. The depths must already agree.
    /// </summary>
    /// <param name="other">The incoming stack.</param>
    /// <param name="types">The session types, for the base chain of session classes.</param>
    /// <returns>True when a slot changed.</returns>
    public bool Merge(StackSimulator other, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(types);
        if (other._items.Count != _items.Count)
        {
            throw new ArgumentException("stacks of different depths cannot merge", nameof(other));
        }

        var changed = false;
        for (var i = 0; i < _items.Count; i++)
        {
            var merged = Join(_items[i], other._items[i], types);
            if (merged != _items[i])
            {
                _items[i] = merged;
                changed = true;
            }

            if (_isThis[i] && !other._isThis[i])
            {
                _isThis[i] = false;
                changed = true;
            }
        }

        return changed;
    }

    private static Type? Join(Type? a, Type? b, TypeTable types)
    {
        if (a is null || b is null)
        {
            return null;
        }

        if (a == b)
        {
            return a;
        }

        if (a == typeof(NullReferenceMarker))
        {
            return StackCompatibility.Category(b) == StackCategory.ObjectReference ? b : null;
        }

        if (b == typeof(NullReferenceMarker))
        {
            return StackCompatibility.Category(a) == StackCategory.ObjectReference ? a : null;
        }

        var category = StackCompatibility.Category(a);
        if (category != StackCompatibility.Category(b))
        {
            return null;
        }

        switch (category)
        {
            case StackCategory.Int32:
                return typeof(int);
            case StackCategory.Int64:
                return typeof(long);
            case StackCategory.NativeInt:
                return typeof(nint);
            case StackCategory.Float:
                return typeof(double);
            case StackCategory.ObjectReference:
                return CommonReference(a, b, types);
            case StackCategory.ByRef:
            case StackCategory.ValueType:
            default:
                // Two byrefs to different pointees, or two different unboxed value types, have no
                // common stack type the model can name.
                return null;
        }
    }

    private static Type? CommonReference(Type a, Type b, TypeTable types)
    {
        static Type Plain(Type t) => BoxedType(t) is not null ? typeof(object) : t;
        a = Plain(a);
        b = Plain(b);
        if (a == b)
        {
            return a;
        }

        if (a.IsGenericParameter || b.IsGenericParameter)
        {
            // Two different parameters share only what their constraints promise; a reference
            // constraint promises object.
            static bool IsReference(Type t) => !t.IsGenericParameter || t.GenericParameterAttributes.HasFlag(System.Reflection.GenericParameterAttributes.ReferenceTypeConstraint);
            return IsReference(a) && IsReference(b) ? typeof(object) : null;
        }

        for (var candidate = a; candidate is not null; candidate = TypeRelations.BaseTypeOf(candidate, types))
        {
            if (candidate.IsInterface)
            {
                break;
            }

            if (TypeRelations.IsAssignable(b, candidate, types))
            {
                return candidate;
            }
        }

        return typeof(object);
    }

    /// <summary>
    /// Replaces the contents of this stack with those of another.
    /// </summary>
    /// <param name="other">The stack to copy from.</param>
    public void CopyFrom(StackSimulator other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _items.Clear();
        _items.AddRange(other._items);
        _isThis.Clear();
        _isThis.AddRange(other._isThis);
    }

    /// <summary>
    /// True when the entry at <paramref name="index"/> (from the bottom) is the <c>this</c> of an
    /// instance member, loaded with <c>ldarg.0</c> and not copied through a local since.
    /// </summary>
    /// <param name="index">The entry index.</param>
    /// <returns>True for <c>this</c>.</returns>
    public bool IsThisAt(int index) => index >= 0 && index < _isThis.Count && _isThis[index];

    /// <summary>
    /// The display name of a stack entry.
    /// </summary>
    /// <param name="type">The entry type.</param>
    /// <returns><c>null</c> for the null marker, <c>?</c> for unknown, otherwise the pretty type name.</returns>
    public static string Name(Type? type) =>
        type == typeof(NullReferenceMarker) ? "null"
        : type == typeof(UnknownReferenceMarker) || BoxedType(type) is not null ? "object"
        : TypeNameFormatter.Pretty(type);

    /// <summary>
    /// The value type behind a <c>box</c> result, or null when the entry is not a boxed value.
    /// </summary>
    /// <param name="type">The stack entry type.</param>
    /// <returns>The boxed value type, or null.</returns>
    public static Type? BoxedType(Type? type) =>
        type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(Boxed<>) ? type.GetGenericArguments()[0] : null;

    /// <summary>
    /// Renders the stack as <c>[a, b, c]</c> with the top on the right.
    /// </summary>
    /// <returns>The rendered stack.</returns>
    public string Render() => _items.Count == 0 ? "[]" : "[" + string.Join(", ", _items.Select(Name)) + "]";

    /// <summary>
    /// Applies the stack effect of a block boundary.
    /// </summary>
    /// <param name="kind">The boundary.</param>
    /// <param name="catchType">The exception type for a catch boundary.</param>
    public void ApplyBlock(BlockKind kind, Type? catchType)
    {
        switch (kind)
        {
            case BlockKind.Try:
                break;
            case BlockKind.Catch:
                Clear();
                Push(catchType ?? typeof(object), false);
                break;
            case BlockKind.Filter:
            case BlockKind.FilterHandler:
                Clear();
                Push(typeof(object), false);
                break;
            case BlockKind.Finally:
            case BlockKind.Fault:
            case BlockKind.End:
                Clear();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Applies an instruction. Throws without changing the stack when it would underflow.
    /// </summary>
    /// <param name="instruction">The instruction.</param>
    /// <param name="context">The parse context, for local and argument types.</param>
    /// <exception cref="ReplException">The instruction pops more values than the stack holds.</exception>
    public void Apply(Instruction instruction, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(context);
        var op = instruction.Op;
        var pops = PopCount(instruction);
        if (pops > _items.Count)
        {
            throw new ReplException($"stack underflow: '{op.Name}' pops {pops} value{(pops == 1 ? "" : "s")} but the stack has {_items.Count}: {Render()}");
        }

        var popped = _items.GetRange(_items.Count - pops, pops);
        var poppedThis = _isThis.GetRange(_isThis.Count - pops, pops);
        _items.RemoveRange(_items.Count - pops, pops);
        _isThis.RemoveRange(_isThis.Count - pops, pops);

        if (op.FlowControl is FlowControl.Throw || op == OpCodes.Leave || op == OpCodes.Leave_S || op == OpCodes.Endfinally
            || op == OpCodes.Endfilter || op == OpCodes.Jmp)
        {
            // Nothing after these is reachable on this path.
            Clear();
            return;
        }

        var loadsThis = instruction.ArgumentIndex == 0 && context.ThisIndex == 0 && op.Name is "ldarg.0" or "ldarg" or "ldarg.s";
        var addressOfThis = instruction.ArgumentIndex == 0 && context.ThisIndex == 0 && op.Name is "ldarga" or "ldarga.s";
        foreach (var t in PushTypes(instruction, popped, context))
        {
            Push(t, loadsThis || addressOfThis || (op == OpCodes.Dup && poppedThis[0]));
        }
    }

    private void Push(Type? type, bool isThis)
    {
        _items.Add(type);
        _isThis.Add(isThis);
    }

    private static int PopCount(Instruction instruction)
    {
        var op = instruction.Op;
        return op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
                or StackBehaviour.Popref_popi_pop1 => 3,
            StackBehaviour.Varpop => VarPop(instruction),
            _ => 0,
        };
    }

    private static int VarPop(Instruction instruction)
    {
        var op = instruction.Op;
        if (op == OpCodes.Ret)
        {
            return instruction.RetPops;
        }

        return instruction.Operand switch
        {
            ResolvedMethod m => m.ArgumentPopCount(op == OpCodes.Newobj),
            CalliSignature s => s.ArgumentPopCount + 1,
            _ => 0,
        };
    }

    private static IEnumerable<Type?> PushTypes(Instruction instruction, List<Type?> popped, ParseContext context)
    {
        var op = instruction.Op;
        var name = op.Name ?? "";

        switch (name)
        {
            case "dup":
                return [popped[0], popped[0]];
            case "ldstr":
                return [typeof(string)];
            case "ldnull":
                return [typeof(NullReferenceMarker)];
            case "ldtoken":
                return [instruction.Operand switch
                {
                    Type => typeof(RuntimeTypeHandle),
                    FieldInfo => typeof(RuntimeFieldHandle),
                    ResolvedMethod => typeof(RuntimeMethodHandle),
                    _ => typeof(RuntimeMethodHandle),
                }];
            case "ldftn":
            case "ldvirtftn":
            case "ldlen":
            case "localloc":
                return [typeof(nint)];
            case "sizeof":
                return [typeof(uint)];
            case "arglist":
                return [typeof(RuntimeArgumentHandle)];
            case "mkrefany":
                return [typeof(TypedReference)];
            case "refanytype":
                return [typeof(RuntimeTypeHandle)];
            case "box":
                return [Box((Type)instruction.Operand!)];
            case "newarr":
                return [((Type)instruction.Operand!).MakeArrayType()];
            case "castclass":
            case "isinst":
            case "unbox.any":
            case "ldobj":
            case "ldelem":
                return [(Type)instruction.Operand!];
            case "unbox":
            case "ldelema":
            case "refanyval":
                return [((Type)instruction.Operand!).MakeByRefType()];
            case "newobj":
                return [((ResolvedMethod)instruction.Operand!).DeclaringType];
            case "ldfld":
            case "ldsfld":
                return [((FieldInfo)instruction.Operand!).FieldType];
            case "ldflda":
            case "ldsflda":
                return [((FieldInfo)instruction.Operand!).FieldType.MakeByRefType()];
            case "ldloc":
            case "ldloc.s":
            case "ldloc.0":
            case "ldloc.1":
            case "ldloc.2":
            case "ldloc.3":
                return [context.Locals[instruction.LocalIndex!.Value].Type];
            case "ldloca":
            case "ldloca.s":
                return [context.Locals[instruction.LocalIndex!.Value].Type.MakeByRefType()];
            case "ldarg":
            case "ldarg.s":
            case "ldarg.0":
            case "ldarg.1":
            case "ldarg.2":
            case "ldarg.3":
                return [context.Arguments[instruction.ArgumentIndex!.Value].Type];
            case "ldarga":
            case "ldarga.s":
                return [context.Arguments[instruction.ArgumentIndex!.Value].Type.MakeByRefType()];
            case "call":
            case "callvirt":
            {
                var returnType = ((ResolvedMethod)instruction.Operand!).ReturnType;
                return returnType == typeof(void) ? [] : [returnType];
            }

            case "calli":
            {
                var signature = (CalliSignature)instruction.Operand!;
                return signature.ReturnType == typeof(void) ? [] : [signature.ReturnType];
            }

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
                return [popped.Count > 0 && popped[0] is { IsByRef: true } or { IsPointer: true } ? popped[0]!.GetElementType() : typeof(UnknownReferenceMarker)];
            case "ldelem.ref":
                return [popped.Count > 1 && popped[0] is { IsArray: true } ? popped[0]!.GetElementType() : typeof(UnknownReferenceMarker)];
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
            StackBehaviour.Pushi => [typeof(int)],
            StackBehaviour.Pushi8 => [typeof(long)],
            StackBehaviour.Pushr4 => [typeof(float)],
            StackBehaviour.Pushr8 => [typeof(double)],
            StackBehaviour.Pushref => [typeof(UnknownReferenceMarker)],
            StackBehaviour.Push1 => [null],
            StackBehaviour.Push1_push1 => [null, null],
            _ => [],
        };
    }

    private static Type Box(Type operand)
    {
        // Boxing a reference type is the identity, and a generic parameter could be either, so
        // only a known value type becomes a boxed entry that remembers what it holds.
        if (operand.IsGenericParameter)
        {
            return typeof(UnknownReferenceMarker);
        }

        if (!operand.IsValueType)
        {
            return operand;
        }

        return typeof(Boxed<>).MakeGenericType(Nullable.GetUnderlyingType(operand) ?? operand);
    }

    private static Type? NumericSuffix(string suffix) => suffix switch
    {
        "i1" => typeof(sbyte),
        "u1" => typeof(byte),
        "i2" => typeof(short),
        "u2" => typeof(ushort),
        "i4" => typeof(int),
        "u4" => typeof(uint),
        "i8" => typeof(long),
        "u8" => typeof(ulong),
        "r4" => typeof(float),
        "r8" or "r" => typeof(double),
        "i" => typeof(nint),
        "u" => typeof(nuint),
        _ => null,
    };

    private static Type? Binary(Type? a, Type? b)
    {
        if (a == typeof(double) || b == typeof(double))
        {
            return typeof(double);
        }

        if (a == typeof(float) || b == typeof(float))
        {
            return typeof(float);
        }

        if (a == typeof(long) || b == typeof(long))
        {
            return typeof(long);
        }

        if (a == typeof(ulong) || b == typeof(ulong))
        {
            return typeof(ulong);
        }

        if (a is { IsByRef: true })
        {
            return a;
        }

        if (b is { IsByRef: true })
        {
            return b;
        }

        if (a is { IsPointer: true })
        {
            return a;
        }

        if (b is { IsPointer: true })
        {
            return b;
        }

        if (a == typeof(nint) || b == typeof(nint))
        {
            return typeof(nint);
        }

        if (a == typeof(nuint) || b == typeof(nuint))
        {
            return typeof(nuint);
        }

        if (a is null || b is null)
        {
            return null;
        }

        return typeof(int);
    }
}
