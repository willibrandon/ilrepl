namespace IlRepl.Engine;

/// <summary>
/// Retains a validation message together with the rule and stack slots that failed it.
/// </summary>
/// <param name="Message">The existing concise diagnostic message.</param>
/// <param name="Requirement">The violated requirement.</param>
/// <param name="Conflicts">The operands to explain.</param>
internal sealed record StackProblem(string Message, string Requirement, IReadOnlyList<StackRequirement> Conflicts)
{
    /// <summary>
    /// Retains a structural requirement that does not identify an individual stack slot.
    /// </summary>
    public static implicit operator StackProblem(string message) => new(message, message, []);
}
