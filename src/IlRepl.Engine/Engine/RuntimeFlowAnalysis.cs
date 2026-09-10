using System.Reflection;
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

    private static StackOperandView<Type> View(CellState state, Instruction instruction, ParseContext context)
    {
        var view = StackSimulator.View(instruction, context);
        if (instruction.Operand is not FieldInfo { IsInitOnly: true } field || instruction.Op.Name is not ("stfld" or "stsfld"))
        {
            return view;
        }

        var scope = new RuntimeBindingScope(context);
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
}
