using System.Reflection;
using System.Reflection.Emit;
using IlRepl.Engine.Binding;

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
    /// Copies the established stack and receiver provenance from a flow state.
    /// </summary>
    internal void CopyFrom(FlowState<Type>? state)
    {
        Clear();
        if (state is { Invalid: false, Values: { } values })
        {
            foreach (var value in values)
            {
                Push(value.Type, value.IsThis);
            }
        }
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
    /// Applies shared stack-transfer rules, preserving the stack when an instruction underflows.
    /// </summary>
    /// <remarks>
    /// Applies an instruction. Throws without changing the stack when it would underflow. The
    /// pops and pushes come from <see cref="StackTransfer{T}"/>, the rules the completer's
    /// preview uses over symbols.
    /// </remarks>
    /// <param name="instruction">The instruction.</param>
    /// <param name="context">The parse context, for local and argument types.</param>
    /// <exception cref="ReplException">The instruction pops more values than the stack holds.</exception>
    public void Apply(Instruction instruction, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(context);
        var op = instruction.Op;
        var view = View(instruction, context);
        var pops = StackTransfer<Type>.PopCount(view);
        if (pops > _items.Count)
        {
            throw new ReplException($"stack underflow: '{op.Name}' pops {pops} value{(pops == 1 ? "" : "s")} but the stack has {_items.Count}: {Render()}");
        }

        var popped = _items.GetRange(_items.Count - pops, pops);
        var poppedThis = _isThis.GetRange(_isThis.Count - pops, pops);
        _items.RemoveRange(_items.Count - pops, pops);
        _isThis.RemoveRange(_isThis.Count - pops, pops);

        if (StackTransfer<Type>.EndsPath(op))
        {
            // Nothing after these is reachable on this path.
            Clear();
            return;
        }

        foreach (var t in RuntimeStackAlgebra.Transfer.PushTypes(view, popped))
        {
            Push(t, view.LoadsThis || view.AddressOfThis || (op == OpCodes.Dup && poppedThis[0]));
        }
    }

    /// <summary>
    /// What the stack transfer needs to know about an instruction, read from its operand.
    /// </summary>
    /// <param name="instruction">The instruction.</param>
    /// <param name="context">The parse context, for local and argument types.</param>
    /// <returns>The view.</returns>
    public static StackOperandView<Type> View(Instruction instruction, ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(context);
        var op = instruction.Op;
        var view = new StackOperandView<Type>
        {
            Op = op,
            RetPops = instruction.RetPops,
            LoadsThis = instruction.ArgumentIndex == 0 && context.ThisIndex == 0 && op.Name is "ldarg.0" or "ldarg" or "ldarg.s",
            AddressOfThis = instruction.ArgumentIndex == 0 && context.ThisIndex == 0 && op.Name is "ldarga" or "ldarga.s",
        };
        if (instruction.LocalIndex is int local && local < context.Locals.Count)
        {
            view = view with { SlotType = context.Locals[local].Type };
        }
        else if (instruction.ArgumentIndex is int argument && argument < context.Arguments.Count)
        {
            view = view with { SlotType = context.Arguments[argument].Type };
        }

        switch (instruction.Operand)
        {
            case Type type:
                return view with { Type = type, Token = StackTokenKind.Type };
            case FieldInfo field:
                return view with
                {
                    FieldType = TypeRelations.SubstituteFor(field.DeclaringType!, field.FieldType),
                    FieldIsStatic = field.IsStatic,
                    DeclaringType = field.DeclaringType,
                    Token = StackTokenKind.Field,
                };
            case ResolvedMethod method:
                return view with
                {
                    ReturnType = method.ReturnType == typeof(void) ? null : method.ReturnType,
                    DeclaringType = method.DeclaringType,
                    ArgumentPops = method.ArgumentPopCount(op == OpCodes.Newobj),
                    ParameterTypes = [.. method.ParameterTypes, .. method.OptionalParameterTypes ?? []],
                    IsInstance = !method.IsStatic && op != OpCodes.Newobj,
                    MethodIsStatic = method.IsStatic,
                    Token = StackTokenKind.Method,
                };
            case CalliSignature signature:
                return view with
                {
                    ReturnType = signature.ReturnType == typeof(void) ? null : signature.ReturnType,
                    ArgumentPops = signature.ArgumentPopCount + 1,
                    ParameterTypes = [.. signature.ParameterTypes, .. signature.OptionalParameterTypes ?? []],
                };
            default:
                return view;
        }
    }

    private void Push(Type? type, bool isThis)
    {
        _items.Add(type);
        _isThis.Add(isThis);
    }
}
