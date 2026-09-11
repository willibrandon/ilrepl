using System.Reflection;
using System.Reflection.Emit;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Adapts decoded IL and metadata clauses to the same analysis used for source bodies.
/// </summary>
public static class StackAnalysis
{
    /// <summary>
    /// The column text for an unknown stack.
    /// </summary>
    public const string Unknown = "?";

    /// <summary>
    /// The column text for an unreachable instruction.
    /// </summary>
    public const string Unreachable = "unreachable";

    /// <summary>
    /// The column text after a proven correctness failure.
    /// </summary>
    public const string Invalid = "invalid";

    /// <summary>
    /// Computes one stack column entry per listing entry, leaving source boundaries blank.
    /// </summary>
    /// <param name="method">The disassembled method.</param>
    /// <returns>The stack after each instruction.</returns>
    public static IReadOnlyList<string?> Run(DisassembledMethod method)
        => Run(method, out _);

    /// <summary>
    /// Computes the stack column and diagnostics with one traversal of the decoded body.
    /// </summary>
    /// <param name="method">The disassembled method.</param>
    /// <param name="diagnostics">The control-flow findings from the same analysis.</param>
    /// <returns>The stack after each instruction.</returns>
    public static IReadOnlyList<string?> Run(DisassembledMethod method, out IReadOnlyList<AnalysisDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(method);
        var analysis = Analyze(method);
        diagnostics = analysis.Diagnostics;
        var rules = RuntimeFlowAnalysis.Rules(method.Context.Types);
        var position = 0;
        return method.Entries.Select(entry => entry.Kind is DisassembledEntryKind.Instruction or DisassembledEntryKind.Raw
            ? rules.Render(analysis.After[position++]) : null).ToArray();
    }

    /// <summary>
    /// Returns findings without interrupting a listing whose metadata or operands are damaged.
    /// </summary>
    /// <param name="method">The disassembled method.</param>
    /// <returns>The control-flow findings.</returns>
    public static IReadOnlyList<AnalysisDiagnostic> Diagnostics(DisassembledMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Analyze(method).Diagnostics;
    }

    /// <summary>
    /// Connects decoded instructions and metadata handlers for shared flow analysis.
    /// </summary>
    internal static FlowResult<Type> Analyze(DisassembledMethod method)
    {
        var entries = method.Entries.Where(entry => entry.Kind is DisassembledEntryKind.Instruction or DisassembledEntryKind.Raw).ToArray();
        var nodes = entries.Select((entry, index) => new FlowNode<Type>(
            new AnalysisLocation(method.Method.Name, index, 0, entry.DisplayText.Length, entry.Offset), entry.DisplayText)
        {
            Instruction = entry.Instruction is { } instruction ? View(instruction, method)
                : entry.Raw is { Op.IsSkipChecksPrefix: true } skipPrefix ? new StackOperandView<Type>
                {
                    Op = OpCodes.Prefix1,
                    ByteOperand = (byte)skipPrefix.Operand.Integer,
                    DecodedPrefixName = skipPrefix.Op.Name,
                }
                : entry.Raw?.Op.Emit is { } op ? new StackOperandView<Type> { Op = op } : null,
            Labels = [IlReader.LabelFor(entry.Offset)],
            Targets = entry.Raw is { } raw ? raw.BranchTarget is { } target
                ? [IlReader.LabelFor(target)] : raw.Operand.SwitchTargets.Select(IlReader.LabelFor).ToArray() : [],
            EffectUnknown = entry.EffectUnknown,
        }).ToArray();
        var hasThis = !method.Method.IsStatic;
        var graph = new FlowGraph<Type>(nodes, typeof(object), complete: true, hasThis: hasThis) { BodyName = method.Method.Name };
        var offsets = entries.Select((entry, index) => (entry.Offset, index)).ToDictionary(pair => pair.Offset, pair => pair.index);

        void Seed(int offset, Type? type)
        {
            if (offsets.TryGetValue(offset, out var index))
            {
                graph.Seeds[index] = type is null ? hasThis ? FlowState<Type>.ThisEntry : FlowState<Type>.Empty
                    : new FlowState<Type>([new FlowValue<Type>(type, [index])], ThisArgumentIsOriginal: hasThis);
            }
        }

        foreach (var clause in method.Clauses)
        {
            switch (clause.Kind)
            {
                case IlClauseKind.Catch:
                    Seed(clause.HandlerStart, clause.CatchType ?? typeof(object));
                    break;
                case IlClauseKind.Filter:
                    Seed(clause.FilterStart ?? clause.HandlerStart, typeof(object));
                    Seed(clause.HandlerStart, typeof(object));
                    break;
                default:
                    Seed(clause.HandlerStart, null);
                    break;
            }
        }

        var groups = new Dictionary<(int, int), int>();
        var sections = new Dictionary<(BlockKind, int, int, int), int>();
        int Position(int offset) => offsets.TryGetValue(offset, out var index) ? index : entries.Length;
        void Section(BlockKind kind, int start, int end, int group)
        {
            var key = (kind, start, end, group);
            if (sections.TryAdd(key, sections.Count))
            {
                graph.Sections[sections[key]] = new FlowRegion(kind, Position(start), Position(end), group);
            }
        }

        foreach (var clause in method.Clauses)
        {
            var key = (clause.TryStart, clause.TryEnd);
            if (!groups.TryGetValue(key, out var group))
            {
                group = groups.Count;
                groups[key] = group;
            }

            Section(BlockKind.Try, clause.TryStart, clause.TryEnd, group);
            var kind = clause.Kind switch
            {
                IlClauseKind.Catch => BlockKind.Catch,
                IlClauseKind.Filter => BlockKind.FilterHandler,
                IlClauseKind.Finally => BlockKind.Finally,
                _ => BlockKind.Fault,
            };
            Section(kind, clause.HandlerStart, clause.HandlerEnd, group);
            if (clause.FilterStart is { } filter)
            {
                Section(BlockKind.Filter, filter, clause.HandlerStart, group);
            }

            graph.Clauses.Add(new FlowClause(group, clause.Kind switch
            {
                IlClauseKind.Catch => BlockKind.Catch,
                IlClauseKind.Filter => BlockKind.Filter,
                IlClauseKind.Finally => BlockKind.Finally,
                _ => BlockKind.Fault,
            }, Position(clause.FilterStart ?? clause.HandlerStart), Position(clause.HandlerStart)));
        }

        for (var index = 0; index < entries.Length; index++)
        {
            graph.Regions[index] = graph.Sections.Where(pair => index >= pair.Value.Start && index < pair.Value.End)
                .OrderBy(pair => pair.Value.Start).ThenByDescending(pair => pair.Value.End).Select(pair => pair.Key).ToArray();
        }

        var returnType = (method.Method as MethodInfo)?.ReturnType;
        var result = new ControlFlowAnalysis<Type>(RuntimeFlowAnalysis.Rules(method.Context.Types)).Run(graph,
            returnType == typeof(void) ? null : returnType, false);
        if (result.End is { Invalid: false, HasUnknownPath: false } && nodes.Length > 0)
        {
            return result with
            {
                Diagnostics = [.. result.Diagnostics, new AnalysisDiagnostic("FLOW020", AnalysisDiagnosticKind.Error,
                    "control falls through the end of the method without a return", nodes[^1].Location, [])],
            };
        }

        return result;
    }

    private static StackOperandView<Type> View(Instruction instruction, DisassembledMethod method)
    {
        var view = StackSimulator.View(instruction, method.Context);
        RuntimeBindingScope? bindingScope = null;
        RuntimeBindingScope Scope() => bindingScope ??= new RuntimeBindingScope(method.Context);
        if (instruction.Op == OpCodes.Jmp && instruction.Operand is ResolvedMethod jump)
        {
            var jumpScope = Scope();
            var source = RuntimeSymbolImporter.Import(method.Method);
            var arguments = method.Context.Arguments.Select(argument =>
                new VariableSymbol(jumpScope.ImportType(argument.Type), argument.Name, false)).ToArray();
            view = view with
            {
                JumpRestriction = JumpCompatibility.Problem(RuntimeFlowAnalysis.JumpTarget(jump, jumpScope), source,
                    arguments, jumpScope.Generics.MethodArguments, source.IsVarArg, jumpScope),
            };
        }

        if (instruction.Operand is not FieldInfo { IsInitOnly: true } field || instruction.Op.Name is not ("stfld" or "stsfld"))
        {
            return view;
        }

        var scope = Scope();
        var symbol = RuntimeSymbolImporter.Import(field);
        var signature = RuntimeSymbolImporter.Import(method.Method);
        return view with
        {
            StoreRestriction = InstructionMemberRules.InitOnlyStoreProblem(symbol, instruction.Op.Name,
                signature, signature.DeclaringType, true, SymbolRenderer.Pretty),
            ReceiverRestriction = InstructionMemberRules.InitOnlyStoreProblem(symbol, instruction.Op.Name,
                signature, signature.DeclaringType, false, SymbolRenderer.Pretty),
        };
    }
}
