using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Adapts independently bound editing bodies to the shared control-flow rules.
/// </summary>
internal static class SymbolFlowAnalysis
{
    /// <summary>
    /// Creates assignment and merge rules over the captured declarations and metadata.
    /// </summary>
    public static FlowTypeRules<TypeSymbol> Rules(IBindingScope scope) => new(
        SymbolStackAlgebra.Instance,
        type => SymbolStackCompatibility.Category(type, scope),
        (from, to) => SymbolRelations.IsAssignable(from, to, scope),
        scope.BaseOf,
        EditingStack.Name,
        SymbolStackAlgebra.BoxedType,
        type =>
        {
            var declaration = scope.ParameterDeclaration(type);
            var constraints = declaration?.Constraints ?? [];
            var reference = declaration?.HasReferenceTypeConstraint == true
                || constraints.Any(constraint => !constraint.IsInterface && !constraint.IsGenericParameter && !constraint.IsValueTypeShape
                    && constraint.Name is not ("Object" or "ValueType" or "Enum"));
            return new FlowParameter<TypeSymbol>(reference, declaration?.HasValueTypeConstraint == true, constraints);
        },
        type => (type.Kind == TypeSymbolKind.SzArray ? 1 : type.Rank, type.Kind == TypeSymbolKind.SzArray),
        (element, rank, vector) => vector ? TypeSymbol.SzArray(element) : TypeSymbol.Array(element, rank, [], []),
        type => scope.EnumUnderlyingType(type) ?? type,
        type => type.DefinitionOrSelf);

    /// <summary>
    /// Analyzes the body without creating runtime definitions.
    /// </summary>
    public static FlowResult<TypeSymbol> Run(EditingBody body, IBindingScope scope, CancellationToken cancellationToken = default)
    {
        var returnType = body.Signature?.ReturnType;
        var declaringType = body.Signature?.DeclaringType;
        var tracksConstructorInitialization = body.Signature is { Name: ".ctor", IsStatic: false }
            && declaringType?.IsValueType == false;
        var graph = new FlowGraph<TypeSymbol>(
            body.FlowNodes, TypeSymbol.Object, hasThis: body.ThisIndex == 0, declaringType: declaringType,
            tracksConstructorInitialization: tracksConstructorInitialization)
        {
            BodyName = body.Signature?.Name ?? "cell",
        };
        return new ControlFlowAnalysis<TypeSymbol>(Rules(scope)).Run(graph,
            SymbolIdentity.Equal(returnType, TypeSymbol.Void) ? null : returnType,
            body.Signature is null, cancellationToken);
    }

    /// <summary>
    /// Extends a converged preview when the appended instruction cannot change earlier edges or exception regions.
    /// </summary>
    /// <param name="body">The editing body including the candidate instruction.</param>
    /// <param name="scope">The candidate instruction's binding scope.</param>
    /// <param name="result">The extended analysis when the incremental step is safe.</param>
    /// <returns>Whether the candidate has a complete incremental analysis.</returns>
    public static bool TryAppend(EditingBody body, IBindingScope scope, out FlowResult<TypeSymbol> result)
    {
        result = null!;
        if (body.Analysis is not { } previous || previous.Before.Length != body.FlowNodes.Count
            || body.FlowNodes[^1] is not { Instruction: { } instruction } node
            || node.Labels.Count != 0 || node.Targets.Count != 0 || node.EffectUnknown
            || body.FlowNodes.Any(candidate => candidate.Block is not null || candidate.ExceptionRegion is not null)
            || instruction.Op.OpCodeType == OpCodeType.Prefix || instruction.DecodedPrefixName is not null
            || instruction.Op == OpCodes.Jmp || instruction.Op == OpCodes.Localloc
            || instruction.Op.FlowControl is not (FlowControl.Next or FlowControl.Call)
            || previous.Diagnostics.Any(diagnostic => diagnostic.Code is "FLOW020" or "FLOW021"))
        {
            return false;
        }

        var declaringType = body.Signature?.DeclaringType;
        var tracksConstructorInitialization = body.Signature is { Name: ".ctor", IsStatic: false }
            && declaringType?.IsValueType == false;
        var graph = new FlowGraph<TypeSymbol>([node], TypeSymbol.Object, hasThis: body.ThisIndex == 0,
            declaringType: declaringType, tracksConstructorInitialization: tracksConstructorInitialization)
        {
            BodyName = body.Signature?.Name ?? "cell",
        };
        var original = previous.End;
        var values = original?.Values;
        var copies = values?.Select(value => value with { Origins = [] }).ToArray();
        if (original is not null)
        {
            graph.Seeds[0] = original with { Values = copies };
        }
        else
        {
            graph.Seeds.Clear();
        }

        var returnType = body.Signature?.ReturnType;
        var step = new ControlFlowAnalysis<TypeSymbol>(Rules(scope)).Run(graph,
            SymbolIdentity.Equal(returnType, TypeSymbol.Void) ? null : returnType, body.Signature is null);
        if (step.Diagnostics.Count > 0)
        {
            // Reanalyze the full body so every explanation retains its original producer locations and graph context.
            return false;
        }

        var position = previous.Before.Length - 1;
        FlowState<TypeSymbol>? Restore(FlowState<TypeSymbol>? current)
        {
            if (current?.Values is not { } currentValues || values is null || copies is null)
            {
                return current;
            }

            FlowValue<TypeSymbol> RestoreValue(FlowValue<TypeSymbol> value)
            {
                for (var index = 0; index < copies.Length; index++)
                {
                    if (ReferenceEquals(copies[index], value))
                    {
                        return values[index];
                    }
                }

                // Dup copies the value record while preserving its producer rather than creating a new value.
                return value with { Origins = instruction.Op == OpCodes.Dup ? values[^1].Origins : [position] };
            }

            return current with { Values = [.. currentValues.Select(RestoreValue)] };
        }

        var before = new FlowState<TypeSymbol>?[previous.Before.Length + 1];
        var after = new FlowState<TypeSymbol>?[previous.After.Length + 1];
        Array.Copy(previous.Before, before, position);
        Array.Copy(previous.After, after, position);
        before[position] = Restore(step.Before[0]);
        after[position] = Restore(step.After[0]);
        before[^1] = Restore(step.End);
        after[^1] = before[^1];
        result = new FlowResult<TypeSymbol>(before, after,
            [.. previous.Diagnostics, .. step.Diagnostics], Math.Max(previous.MaxStack, step.MaxStack));
        return true;
    }

    /// <summary>
    /// Analyzes a captured body with cancellation points within the fixed-point worklist.
    /// </summary>
    public static ValueTask<FlowResult<TypeSymbol>> RunAsync(
        EditingBody body,
        IBindingScope scope,
        CancellationToken cancellationToken = default)
    {
        var returnType = body.Signature?.ReturnType;
        var declaringType = body.Signature?.DeclaringType;
        var tracksConstructorInitialization = body.Signature is { Name: ".ctor", IsStatic: false }
            && declaringType?.IsValueType == false;
        var graph = new FlowGraph<TypeSymbol>(
            body.FlowNodes, TypeSymbol.Object, hasThis: body.ThisIndex == 0, declaringType: declaringType,
            tracksConstructorInitialization: tracksConstructorInitialization)
        {
            BodyName = body.Signature?.Name ?? "cell",
        };
        return new ControlFlowAnalysis<TypeSymbol>(Rules(scope)).RunAsync(graph,
            SymbolIdentity.Equal(returnType, TypeSymbol.Void) ? null : returnType,
            body.Signature is null, cancellationToken);
    }
}
