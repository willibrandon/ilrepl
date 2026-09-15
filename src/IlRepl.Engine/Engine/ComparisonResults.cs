using IlRepl.Protocol;

namespace IlRepl.Engine;

/// <summary>
/// Compares typed observations while distinguishing input differences and incomplete executions.
/// </summary>
public static class ComparisonResults
{
    /// <summary>
    /// Combines the independently observed sides without executing user equality code.
    /// </summary>
    /// <param name="package">The captured versions and starting conditions.</param>
    /// <param name="original">The original-side observations.</param>
    /// <param name="edited">The edited-side observations.</param>
    /// <returns>The comparison outcome and both complete reports.</returns>
    public static ComparisonReply Compare(ComparisonPackage package, ComparisonSide original, ComparisonSide edited)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        var complete = Complete(original) && Complete(edited);
        var sameInputs = original.Invocations.Count == edited.Invocations.Count
            && original.Invocations.Zip(edited.Invocations).All(pair => Equal(pair.First.Inputs, pair.Second.Inputs));
        var sameOutputs = original.StandardOutput == edited.StandardOutput && original.StandardError == edited.StandardError
            && original.Exception == edited.Exception && Equal(original.Result, edited.Result)
            && original.Invocations.Zip(edited.Invocations).All(pair => pair.First.Exception == pair.Second.Exception
                && Equal(pair.First.Outputs, pair.Second.Outputs));
        var outcome = !complete ? "incomplete" : !sameInputs ? "different-inputs" : sameOutputs ? "match" : "different";
        return new ComparisonReply(package.Name, package.BaselineFingerprint, package.Revision, outcome, package.StartingState,
            original, edited);
    }

    private static bool Complete(ComparisonSide side) => side.Outcome == "completed" && side.Invocations.Count != 0
        && Available(side.Result) && CompleteException(side.Exception)
        && side.Invocations.All(invocation => invocation.Inputs.All(member => Available(member.Value))
            && invocation.Outputs.All(member => Available(member.Value)) && CompleteException(invocation.Exception));

    private static bool CompleteException(ObservedException? exception) => exception is null
        || (exception.Problem is null && exception.Fields.All(member => Available(member.Value)) && CompleteException(exception.Inner)
            && exception.AdditionalInnerExceptions.All(CompleteException));

    private static bool Available(ObservedValue? value) => value is null || (value.Kind != "unavailable" && value.Members.All(member =>
        Available(member.Value)));

    private static bool Equal(IReadOnlyList<ObservedMember> left, IReadOnlyList<ObservedMember> right) => left.Count == right.Count
        && left.Zip(right).All(pair => pair.First.Name == pair.Second.Name && Equal(pair.First.Value, pair.Second.Value));

    private static bool Equal(ObservedValue? left, ObservedValue? right) => left is null ? right is null
        : right is not null && left.Kind == right.Kind && left.Type == right.Type && left.Value == right.Value
            && left.Identity == right.Identity && Equal(left.Members, right.Members);
}
