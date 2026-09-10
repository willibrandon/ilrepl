using IlRepl.Engine.Binding;

namespace IlRepl.Repl;

/// <summary>
/// Applies a generic owner's constraints to one edited argument while retaining the other supplied arguments.
/// </summary>
internal sealed record GenericArgumentContext
{
    /// <summary>
    /// The exact generic definition whose argument is being edited.
    /// </summary>
    public required GenericCompletionTarget Target { get; init; }

    /// <summary>
    /// The already bound arguments, with null for components the user has not completed.
    /// </summary>
    public required IReadOnlyList<TypeSymbol?> Arguments { get; init; }

    /// <summary>
    /// The argument being replaced.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Tests special constraints immediately and defers only constraints involving an unfinished argument.
    /// </summary>
    /// <param name="candidate">The proposed argument type.</param>
    /// <param name="scope">The captured context.</param>
    /// <returns>Whether every currently decidable constraint permits the argument.</returns>
    public bool Allows(TypeSymbol candidate, IBindingScope scope)
    {
        if (Index < 0 || Index >= Target.Parameters.Count)
        {
            return false;
        }

        var supplied = Arguments.ToArray();
        supplied[Index] = candidate;
        var parameters = Target.Parameters;
        var pending = parameters.Where(parameter => supplied[parameter.Position] is null)
            .Select(parameter => parameter.AsType).ToHashSet();
        var assignment = parameters.ToDictionary(parameter => parameter.AsType, parameter => supplied[parameter.Position]);
        TypeSymbol Substitute(TypeSymbol type)
        {
            var declared = Target.Method?.DeclaringType is { IsConstructed: true } declaring
                ? SymbolRelations.SubstituteTypeParameters(type, declaring.Element!.Definition, declaring.Arguments) : type;
            return SymbolRelations.Rewrite(declared, part => assignment.GetValueOrDefault(part));
        }

        bool DependsOnPending(TypeSymbol type)
        {
            var found = false;
            SymbolRelations.Rewrite(type, part =>
            {
                found |= pending.Contains(part);
                return null;
            });
            return found;
        }

        foreach (var parameter in parameters)
        {
            if (supplied[parameter.Position] is not { } argument)
            {
                continue;
            }

            var ready = parameter with { Constraints = parameter.Constraints
                .Where(constraint => !DependsOnPending(Substitute(constraint))).ToArray() };
            if (!GenericConstraints.Satisfies(ready, argument, Substitute, scope))
            {
                return false;
            }
        }

        return true;
    }
}
