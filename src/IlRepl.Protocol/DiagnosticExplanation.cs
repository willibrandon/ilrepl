namespace IlRepl.Protocol;

/// <summary>
/// Carries the same requirements and source evidence used to reject an instruction.
/// </summary>
/// <param name="Instruction">The instruction's help, when its opcode is known.</param>
/// <param name="Requirement">The violated stack or structural requirement.</param>
/// <param name="Stack">The incoming stack, when analysis can describe it.</param>
/// <param name="Conflicts">The conflicting operands and their producers.</param>
/// <param name="Incoming">The separate predecessors of an incompatible join.</param>
public sealed record DiagnosticExplanation(
    InstructionHelp? Instruction,
    string Requirement,
    AnalyzedStack? Stack,
    IReadOnlyList<StackConflict> Conflicts,
    IReadOnlyList<DiagnosticStackPath> Incoming)
{
    /// <summary>
    /// The rejected source, distinguishing the current document from accepted or imported instructions.
    /// </summary>
    public AnalysisSource? Source { get; init; }
}
