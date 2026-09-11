using System.Reflection;
using System.Reflection.Emit;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Adapts accepted source entries to the shared control-flow model.
/// </summary>
internal static class RuntimeFlowAnalysis
{
    /// <summary>
    /// Creates type rules that recognize accepted and provisional session identities.
    /// </summary>
    public static FlowTypeRules<Type> Rules(TypeTable types) => new(
        RuntimeStackAlgebra.Instance,
        StackCompatibility.Category,
        (from, to) => TypeRelations.IsAssignable(from, to, types),
        type => TypeRelations.BaseTypeOf(type, types),
        StackSimulator.Name,
        StackSimulator.BoxedType,
        RuntimeGenericConstraints.Read,
        type => (type.GetArrayRank(), type.IsSZArray),
        (element, rank, vector) => vector ? element.MakeArrayType() : element.MakeArrayType(rank),
        type => UnderlyingType(type, types));

    private static Type UnderlyingType(Type type, TypeTable types)
    {
        if (types.TryGetMembers(type, out var members))
        {
            return members.BaseType == typeof(Enum)
                ? members.Fields.FirstOrDefault(field => field.Declaration.Name == "value__").Declaration?.Type ?? type : type;
        }

        return !type.IsGenericParameter && type.IsEnum ? Enum.GetUnderlyingType(type) : type;
    }

    /// <summary>
    /// Analyzes the source body, retaining one result position for every accepted entry.
    /// </summary>
    public static FlowResult<Type> Run(CellState state, IReadOnlyList<CellEntry> entries, CancellationToken cancellationToken = default)
    {
        var context = state.Context;
        var body = state.Signature?.Name ?? "cell";
        var nodes = entries.Select((entry, index) => new FlowNode<Type>(
            entry.Location ?? new AnalysisLocation(body, index, 0, entry.Source.Length), entry.Source)
        {
            Instruction = entry.Instruction is { } instruction ? View(state, instruction, context) : null,
            Labels = entry.Labels,
            Targets = entry.Instruction?.Kind switch
            {
                OperandKind.Label => [(string)entry.Instruction.Operand!],
                OperandKind.Labels => (string[])entry.Instruction.Operand!,
                _ => [],
            },
            Block = entry.Block,
            CatchType = entry.CatchType,
        }).ToArray();
        var returnType = state.Signature?.ReturnType;
        return new ControlFlowAnalysis<Type>(Rules(state.Types)).Run(new FlowGraph<Type>(nodes, typeof(object)) { BodyName = body },
            returnType == typeof(void) ? null : returnType, !state.IsMethod, cancellationToken);
    }

    /// <summary>
    /// Extends a converged body when the new instruction cannot change any earlier edge or region.
    /// </summary>
    /// <param name="state">The live body receiving the instruction.</param>
    /// <param name="previous">The converged result before the instruction.</param>
    /// <param name="entry">The candidate source entry.</param>
    /// <param name="result">The extended result when an incremental step is safe.</param>
    /// <returns>Whether <paramref name="result"/> contains the complete analysis.</returns>
    public static bool TryAppend(CellState state, FlowResult<Type> previous, CellEntry entry, out FlowResult<Type> result)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(entry);
        result = null!;
        if (entry.Instruction is not { } instruction || entry.Labels.Count != 0
            || instruction.Kind is OperandKind.Label or OperandKind.Labels
            || instruction.Op.OpCodeType == OpCodeType.Prefix
            || instruction.Op == OpCodes.Jmp
            || instruction.Op == OpCodes.Localloc
            || instruction.Op.FlowControl is not (FlowControl.Next or FlowControl.Call)
            || previous.Diagnostics.Any(diagnostic => diagnostic.Code is "FLOW020" or "FLOW021"))
        {
            return false;
        }

        var position = previous.Before.Length - 1;
        var body = state.Signature?.Name ?? "cell";
        var location = entry.Location ?? new AnalysisLocation(body, position, 0, entry.Source.Length);
        var node = new FlowNode<Type>(location, entry.Source)
        {
            Instruction = View(state, instruction, state.Context),
        };
        var graph = new FlowGraph<Type>([node], typeof(object)) { BodyName = body };
        var original = previous.End;
        var values = original?.Values;
        var copies = values?.Select(value => value with { Origins = [] }).ToArray();
        if (original is not null)
        {
            graph.Seeds[0] = original with { Values = copies };
        }

        var returnType = state.Signature?.ReturnType;
        var step = new ControlFlowAnalysis<Type>(Rules(state.Types)).Run(graph,
            returnType == typeof(void) ? null : returnType, !state.IsMethod);
        if (step.Diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error))
        {
            return false;
        }

        var diagnostics = previous.Diagnostics.Concat(step.Diagnostics).ToArray();
        if (original is null)
        {
            result = Append(previous, null, null, null, diagnostics, previous.MaxStack);
            return true;
        }

        FlowState<Type>? Restore(FlowState<Type>? current)
        {
            if (current?.Values is not { } currentValues || values is null || copies is null)
            {
                return current;
            }

            FlowValue<Type> RestoreValue(FlowValue<Type> value)
            {
                for (var index = 0; index < copies.Length; index++)
                {
                    if (ReferenceEquals(copies[index], value))
                    {
                        return values[index];
                    }
                }

                return value with { Origins = [position] };
            }

            return current with
            {
                Values = [.. currentValues.Select(RestoreValue)],
            };
        }

        result = Append(previous, Restore(step.Before[0]), Restore(step.After[0]), Restore(step.End), diagnostics,
            Math.Max(previous.MaxStack, step.MaxStack));
        return true;
    }

    private static FlowResult<Type> Append(FlowResult<Type> previous, FlowState<Type>? beforeCurrent,
        FlowState<Type>? afterCurrent, FlowState<Type>? end, IReadOnlyList<AnalysisDiagnostic> diagnostics, int maxStack)
    {
        var position = previous.Before.Length - 1;
        var before = new FlowState<Type>?[previous.Before.Length + 1];
        var after = new FlowState<Type>?[previous.After.Length + 1];
        Array.Copy(previous.Before, before, position);
        Array.Copy(previous.After, after, position);
        before[position] = beforeCurrent;
        after[position] = afterCurrent;
        before[^1] = end;
        after[^1] = end;
        return new FlowResult<Type>(before, after, diagnostics, maxStack);
    }

    private static StackOperandView<Type> View(CellState state, Instruction instruction, ParseContext context)
    {
        var view = StackSimulator.View(instruction, context);
        RuntimeBindingScope? bindingScope = null;
        RuntimeBindingScope Scope() => bindingScope ??= new RuntimeBindingScope(context);
        if (instruction.Op == OpCodes.Jmp && instruction.Operand is ResolvedMethod jump)
        {
            var jumpScope = Scope();
            var sourceOwner = state.Member is null ? null : jumpScope.ImportType(state.Member.Owner);
            var source = state.Signature is null ? null : RuntimeSymbolImporter.Import(state.Signature, sourceOwner,
                RuntimeDefinitions.OfDeclaration(state.Signature, 0), MethodSymbolSource.Declared, true);
            var arguments = context.Arguments.Select(argument =>
                new VariableSymbol(jumpScope.ImportType(argument.Type), argument.Name, false))
                .ToArray();
            view = view with
            {
                JumpRestriction = JumpCompatibility.Problem(JumpTarget(jump, jumpScope), source, arguments,
                    jumpScope.Generics.MethodArguments, state.IsVarArg, jumpScope),
            };
        }

        if (instruction.Operand is not FieldInfo { IsInitOnly: true } field || instruction.Op.Name is not ("stfld" or "stsfld"))
        {
            return view;
        }

        var scope = Scope();
        var owner = state.Member is null ? null : scope.ImportType(state.Member.Owner);
        var signature = state.Signature is null ? null
            : RuntimeSymbolImporter.Import(state.Signature, owner, default, MethodSymbolSource.Declared, true);
        var symbol = RuntimeSymbolImporter.Import(field);
        return view with
        {
            StoreRestriction = InstructionMemberRules.InitOnlyStoreProblem(symbol, instruction.Op.Name,
                signature, owner, true, scope.Pretty),
            ReceiverRestriction = InstructionMemberRules.InitOnlyStoreProblem(symbol, instruction.Op.Name,
                signature, owner, false, scope.Pretty),
        };
    }

    /// <summary>
    /// Restores the symbol retained by a runtime method operand.
    /// </summary>
    internal static MethodSymbol JumpTarget(ResolvedMethod method, RuntimeBindingScope scope)
    {
        if (method.Definition is { } session)
        {
            return RuntimeSymbolImporter.Import(session, null, RuntimeDefinitions.OfDeclaration(session, 0),
                MethodSymbolSource.Session, true);
        }

        if (method.Declared is { } declared)
        {
            var declaring = scope.ImportType(method.DeclaringType!);
            var identity = method.Method is null ? RuntimeDefinitions.OfDeclaration(declared, 0)
                : RuntimeDefinitions.Of(method.Method);
            var symbol = RuntimeSymbolImporter.Import(declared, declaring, identity, MethodSymbolSource.Declared, true);
            return method.InstantiationArguments.Count == 0 ? symbol : symbol.With(declaring, symbol.ReturnType,
                symbol.Parameters, [.. method.InstantiationArguments.Select(scope.ImportType)]);
        }

        return RuntimeSymbolImporter.Import(method.Method!, scope.ImportType(method.DeclaringType!));
    }
}
