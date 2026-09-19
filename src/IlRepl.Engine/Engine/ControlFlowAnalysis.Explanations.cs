using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Presents established stack evidence without rerunning validation or retaining runtime objects in replies.
/// </summary>
internal sealed partial class ControlFlowAnalysis<T> where T : class
{
    private static StackProblem Failure(string message, string requirement, params StackRequirement[] conflicts) =>
        new(message, requirement, conflicts);

    private static StackProblem OperandFailure(string message, int index, string role, string expected) =>
        Failure(message, role + " must be " + expected, new StackRequirement(index, role, expected));

    private static StackProblem ShapeFailure(string message, string requirement, int count) =>
        Failure(message, requirement, count == 0 ? [new StackRequirement(-1, "stack", requirement)]
            : Enumerable.Range(0, count).Select(index => new StackRequirement(index, "stack slot " + index, requirement)).ToArray());

    private StackProblem BinaryFailure(string message, string op, int count, T left, T right)
    {
        var categories = Enum.GetValues<StackCategory>();
        string Allowed(bool first)
        {
            var matching = categories.Where(category => first
                ? FlowNumericRules.Binary(op, category, _types.Category(right))
                : FlowNumericRules.Binary(op, _types.Category(left), category)).ToArray();
            if (matching.Length == 0)
            {
                matching = categories.Where(category => categories.Any(other => first
                    ? FlowNumericRules.Binary(op, category, other) : FlowNumericRules.Binary(op, other, category))).ToArray();
            }

            return string.Join(" or ", matching.Select(CategoryName));
        }

        return Failure(message, "compatible operand types for " + op,
            new StackRequirement(count - 2, "left operand", Allowed(true)),
            new StackRequirement(count - 1, "right operand", Allowed(false)));
    }

    private static string CategoryName(StackCategory category) => category switch
    {
        StackCategory.Int32 => "int32",
        StackCategory.Int64 => "int64",
        StackCategory.NativeInt => "native int",
        StackCategory.Float => "floating point",
        StackCategory.ByRef => "managed pointer",
        StackCategory.ObjectReference => "object reference",
        _ => "value type",
    };

    private AnalysisDiagnostic Explain(
        AnalysisDiagnostic diagnostic,
        int position,
        IReadOnlyList<FlowNode<T>> nodes,
        FlowState<T>? state,
        StackProblem? problem,
        IReadOnlyList<(int Position, FlowState<T> State)> incoming,
        int? returnArity,
        FlowState<T>? outgoing)
    {
        if (position < 0 || position >= nodes.Count)
        {
            return position == nodes.Count && nodes.Count > 0
                ? diagnostic with { Location = SourceLocation(nodes[^1]) } : diagnostic;
        }

        var instruction = InstructionReference.For(nodes, position, _types, state, returnArity, outgoing);
        var conflicts = problem?.Conflicts.Select(requirement => ExplainValue(requirement, state, nodes)).ToArray() ?? [];
        var paths = incoming.Select(path => new DiagnosticStackPath(Source(nodes, path.Position), Present(path.State),
            (path.State.Values ?? []).Select((value, slot) => ExplainValue(
                new StackRequirement(slot, "stack slot " + slot, "compatible type at the join"), path.State, nodes)).ToArray()))
            .ToArray();
        return diagnostic with
        {
            Location = SourceLocation(nodes[position]),
            Explanation = new DiagnosticExplanation(instruction, problem?.Requirement ?? diagnostic.Message,
                state is null ? new AnalyzedStack(AnalyzedStackKind.Unreachable, []) : Present(state), conflicts, paths)
            {
                Source = Source(nodes, position),
            },
        };
    }

    private StackConflict ExplainValue(StackRequirement requirement, FlowState<T>? state, IReadOnlyList<FlowNode<T>> nodes)
    {
        var value = requirement.Index >= 0 && state?.Values is { } values && requirement.Index < values.Length
            ? values[requirement.Index] : null;
        return new StackConflict(value is null ? -1 : requirement.Index, requirement.Role, requirement.Expected,
            value is null ? null : _types.Name(value.Type),
            value?.Origins.Distinct().Order().Select(origin => Source(nodes, origin)).ToArray() ?? []);
    }

    private AnalyzedStack Present(FlowState<T> state) =>
        new(state.Kind, state.Values?.Select(value => _types.Name(value.Type)).ToArray() ?? []);

    private static AnalysisSource Source(IReadOnlyList<FlowNode<T>> nodes, int position)
    {
        if (position < 0 || position >= nodes.Count)
        {
            return new AnalysisSource(new AnalysisLocation("entry", -1, 0, 0), "method entry", AnalysisSourceKind.Synthetic);
        }

        var node = nodes[position];
        var handler = node.Block is BlockKind.Catch or BlockKind.Filter;
        var text = handler ? "exception-handler entry: " + node.Source
            : node.Synthetic ? "implicit return: " + node.Source : node.Source;
        return new AnalysisSource(SourceLocation(node), text,
            node.Synthetic || handler ? AnalysisSourceKind.Synthetic : node.SourceKind);
    }

    private static AnalysisLocation SourceLocation(FlowNode<T> node) =>
        node.SourceLocation ?? new AnalysisLocation(node.Location.Body, -1, 0, 0);
}
