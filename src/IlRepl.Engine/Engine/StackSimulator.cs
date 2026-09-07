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
    /// Copies the stack.
    /// </summary>
    /// <returns>An independent copy.</returns>
    public StackSimulator Clone()
    {
        var copy = new StackSimulator();
        copy._items.AddRange(_items);
        return copy;
    }

    /// <summary>
    /// Empties the stack.
    /// </summary>
    public void Clear() => _items.Clear();

    /// <summary>
    /// Replaces the contents of this stack with those of another.
    /// </summary>
    /// <param name="other">The stack to copy from.</param>
    public void CopyFrom(StackSimulator other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _items.Clear();
        _items.AddRange(other._items);
    }

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
                _items.Clear();
                _items.Add(catchType ?? typeof(object));
                break;
            case BlockKind.Filter:
            case BlockKind.FilterHandler:
                _items.Clear();
                _items.Add(typeof(object));
                break;
            case BlockKind.Finally:
            case BlockKind.Fault:
            case BlockKind.End:
                _items.Clear();
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
        _items.RemoveRange(_items.Count - pops, pops);

        if (op.FlowControl is FlowControl.Throw || op == OpCodes.Leave || op == OpCodes.Leave_S || op == OpCodes.Endfinally
            || op == OpCodes.Endfilter || op == OpCodes.Jmp)
        {
            // Nothing after these is reachable on this path.
            _items.Clear();
            return;
        }

        foreach (var t in PushTypes(instruction, popped, context))
        {
            _items.Add(t);
        }
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
