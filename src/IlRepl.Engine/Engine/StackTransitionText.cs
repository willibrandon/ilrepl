using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// Describes a candidate instruction using the same stack transfer as execution and editing replay.
/// </summary>
public static class StackTransitionText
{
    /// <summary>
    /// Formats the consumed and produced values without requiring a complete evaluation stack.
    /// </summary>
    /// <param name="instruction">The confirmed instruction.</param>
    /// <param name="context">The editing context and preceding prefix.</param>
    /// <returns>The instruction's stack transition.</returns>
    public static string Format(BoundInstruction instruction, EditingView context)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(context);
        var view = EditingStack.View(instruction, context.Scope);
        var count = StackTransfer<TypeSymbol>.PopCount(view);
        var popped = new TypeSymbol?[count];
        for (var index = 0; index < count; index++)
        {
            var source = context.Stack.Count - count + index;
            popped[index] = source >= 0 ? context.Stack[source] : null;
        }

        var op = instruction.Op.Name;
        if (instruction.Operand.Method is { Method: var method } && op is "call" or "callvirt" or "newobj" or "ldvirtftn")
        {
            var offset = !method.IsStatic && op != "newobj" ? 1 : 0;
            if (offset != 0)
            {
                popped[0] = Receiver(method.DeclaringType, context);
            }

            if (op != "ldvirtftn")
            {
                for (var index = 0; index < method.Parameters.Count; index++)
                {
                    popped[offset + index] = method.Parameters[index].Type;
                }

                var optional = instruction.Operand.Method.OptionalParameterTypes;
                for (var index = 0; index < (optional?.Count ?? 0); index++)
                {
                    popped[offset + method.Parameters.Count + index] = optional![index];
                }
            }
        }
        else if (instruction.Operand.Field is { } field && op != "ldtoken")
        {
            if (!field.IsStatic && count != 0)
            {
                popped[0] = Receiver(field.DeclaringType, context);
            }

            if (op is "stfld" or "stsfld")
            {
                popped[^1] = field.FieldType;
            }
        }
        else if (op is "stloc" or "stloc.s" or "starg" or "starg.s")
        {
            popped[0] = view.SlotType;
        }

        var pushed = SymbolStackAlgebra.Transfer.PushTypes(view, popped);
        return "[" + string.Join(", ", popped.Select(Name)) + "] → "
            + (pushed.Count == 1 ? Name(pushed[0]) : "[" + string.Join(", ", pushed.Select(Name)) + "]");
    }

    private static TypeSymbol? Receiver(TypeSymbol? owner, EditingView context)
    {
        if (context.PrecedingInstruction is { Op.Name: "constrained.", Operand.Type: { } constraint })
        {
            return TypeSymbol.ByRef(constraint);
        }

        return owner is { IsValueTypeShape: true } ? TypeSymbol.ByRef(owner) : owner;
    }

    private static string Name(TypeSymbol? type) => SymbolIdentity.Equal(type, SymbolStackAlgebra.Instance.NullReference)
        ? "null"
        : SymbolIdentity.Equal(type, SymbolStackAlgebra.Instance.UnknownReference) || SymbolStackAlgebra.BoxedType(type) is not null
            ? "object" : SymbolRenderer.Pretty(type);
}
