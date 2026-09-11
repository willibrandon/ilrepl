using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// The preview's evaluation stack, using the runtime model's shared transfer over symbols.
/// </summary>
internal sealed class EditingStack
{
    private readonly List<TypeSymbol?> _items = [];
    private readonly List<bool> _receivers = [];

    /// <summary>
    /// The stack entries from bottom to top.
    /// </summary>
    public IReadOnlyList<TypeSymbol?> Items => _items;

    /// <summary>
    /// Copies the stack and receiver provenance for a transactional line checkpoint.
    /// </summary>
    /// <returns>The independent copy.</returns>
    public EditingStack Clone()
    {
        var clone = new EditingStack();
        clone._items.AddRange(_items);
        clone._receivers.AddRange(_receivers);
        return clone;
    }

    /// <summary>
    /// Empties the stack at an execution or handler boundary.
    /// </summary>
    public void Clear()
    {
        _items.Clear();
        _receivers.Clear();
    }

    /// <summary>
    /// Adds an entry at a handler boundary.
    /// </summary>
    /// <param name="type">The exception type, or null for unknown.</param>
    public void Push(TypeSymbol? type)
    {
        _items.Add(type);
        _receivers.Add(false);
    }

    /// <summary>
    /// Tests whether the specified slot is still the original instance receiver.
    /// </summary>
    /// <param name="index">The bottom-based index.</param>
    /// <returns>Whether the value is this.</returns>
    public bool IsThisAt(int index) => index >= 0 && index < _receivers.Count && _receivers[index];

    /// <summary>
    /// Applies the shared opcode transfer after checking for stack underflow.
    /// </summary>
    /// <param name="instruction">The bound instruction.</param>
    /// <param name="scope">The body's variables.</param>
    /// <param name="retPops">The values consumed by an inline return.</param>
    public void Apply(BoundInstruction instruction, IBindingScope scope, int retPops = 0)
    {
        var view = View(instruction, scope, retPops);
        var pops = StackTransfer<TypeSymbol>.PopCount(view);
        if (pops > _items.Count)
        {
            var suffix = pops == 1 ? "" : "s";
            throw new ReplException(
                $"stack underflow: '{instruction.Op.Name}' pops {pops} value{suffix} but the stack has {_items.Count}: {Render()}");
        }

        var popped = _items.GetRange(_items.Count - pops, pops);
        var duplicateReceiver = instruction.Op == OpCodes.Dup && _receivers[^1];
        _items.RemoveRange(_items.Count - pops, pops);
        _receivers.RemoveRange(_receivers.Count - pops, pops);
        if (StackTransfer<TypeSymbol>.EndsPath(instruction.Op))
        {
            Clear();
            return;
        }

        foreach (var type in SymbolStackAlgebra.Transfer.PushTypes(view, popped))
        {
            _items.Add(type);
            _receivers.Add(view.LoadsThis || view.AddressOfThis || duplicateReceiver);
        }
    }

    /// <summary>
    /// Describes an operand's stack effect without materializing any runtime types or members.
    /// </summary>
    /// <param name="instruction">The instruction.</param>
    /// <param name="scope">The variables in scope.</param>
    /// <param name="retPops">The return value count.</param>
    /// <returns>The shared transfer input.</returns>
    public static StackOperandView<TypeSymbol> View(BoundInstruction instruction, IBindingScope scope, int retPops = 0)
    {
        var op = instruction.Op;
        var view = new StackOperandView<TypeSymbol>
        {
            Op = op,
            ByteOperand = instruction.Operand.Kind == OperandKind.Byte && instruction.Operand.Value is byte value ? value : null,
            RetPops = retPops,
            LoadsThis = instruction.ArgumentIndex == 0 && scope.ThisIndex == 0 && op.Name is "ldarg.0" or "ldarg" or "ldarg.s",
            AddressOfThis = instruction.ArgumentIndex == 0 && scope.ThisIndex == 0 && op.Name is "ldarga" or "ldarga.s",
        };
        if (instruction.LocalIndex is int local && local < scope.Locals.Count)
        {
            view = view with { SlotType = scope.Locals[local].Type };
        }
        else if (instruction.ArgumentIndex is int argument && argument < scope.Arguments.Count)
        {
            view = view with { SlotType = scope.Arguments[argument].Type };
        }

        var operand = instruction.Operand;
        if (operand.Type is { } type)
        {
            return view with { Type = type, Token = StackTokenKind.Type };
        }

        if (operand.Field is { } field)
        {
            return view with
            {
                FieldType = field.FieldType,
                FieldIsStatic = field.IsStatic,
                DeclaringType = field.DeclaringType,
                Token = StackTokenKind.Field,
            };
        }

        if (operand.Method is { } bound)
        {
            var method = bound.Method;
            return view with
            {
                ReturnType = SymbolIdentity.Equal(method.ReturnType, TypeSymbol.Void) ? null : method.ReturnType,
                DeclaringType = method.DeclaringType,
                ArgumentPops = method.Parameters.Count + (bound.OptionalParameterTypes?.Count ?? 0)
                    + (!method.IsStatic && op != OpCodes.Newobj ? 1 : 0),
                ParameterTypes = [.. method.Parameters.Select(parameter => parameter.Type), .. bound.OptionalParameterTypes ?? []],
                IsInstance = !method.IsStatic && op != OpCodes.Newobj,
                MethodIsStatic = method.IsStatic,
                Token = StackTokenKind.Method,
            };
        }

        return operand.Signature is { } signature ? view with
        {
            ReturnType = SymbolIdentity.Equal(signature.ReturnType, TypeSymbol.Void) ? null : signature.ReturnType,
            ArgumentPops = signature.ArgumentPopCount + 1,
            ParameterTypes = signature.Parameters,
        } : view;
    }

    /// <summary>
    /// Renders the same stack names as the runtime model.
    /// </summary>
    /// <returns>The bracketed stack.</returns>
    public string Render() => "[" + string.Join(", ", _items.Select(Name)) + "]";

    /// <summary>
    /// Copies the established stack and receiver provenance from a flow state.
    /// </summary>
    internal void CopyFrom(FlowState<TypeSymbol>? state)
    {
        Clear();
        if (state is { Invalid: false, Values: { } values })
        {
            foreach (var value in values)
            {
                _items.Add(value.Type);
                _receivers.Add(value.IsThis);
            }
        }
    }

    /// <summary>
    /// Formats a stack value while keeping boxed markers out of user-facing type names.
    /// </summary>
    internal static string Name(TypeSymbol? type) => SymbolIdentity.Equal(type, SymbolStackAlgebra.Instance.NullReference)
        ? "null"
        : SymbolIdentity.Equal(type, SymbolStackAlgebra.Instance.UnknownReference) || SymbolStackAlgebra.BoxedType(type) is not null
            ? "object" : SymbolRenderer.Pretty(type);
}
