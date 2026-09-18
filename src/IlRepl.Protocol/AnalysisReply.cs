namespace IlRepl.Protocol;

/// <summary>
/// Returns source findings and the caret stack for one document and binding snapshot.
/// </summary>
/// <param name="DocumentVersion">The document that was analyzed.</param>
/// <param name="Revision">The captured session revision.</param>
/// <param name="BindingEpoch">The loaded-binding snapshot.</param>
/// <param name="AssemblyVersion">The captured searchable-assembly version.</param>
/// <param name="Stack">The incoming stack at the caret, or null outside a body.</param>
/// <param name="BeforeInstruction">Whether the caret is on an instruction line.</param>
/// <param name="Diagnostics">Findings and their related source locations.</param>
public sealed record AnalysisReply(
    long DocumentVersion,
    long Revision,
    long BindingEpoch,
    long AssemblyVersion,
    AnalyzedStack? Stack,
    bool BeforeInstruction,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics)
{
    /// <summary>
    /// Explains the instruction at the caret without requiring the completion palette to be open.
    /// </summary>
    public InstructionHelp? InstructionHelp { get; init; }

    /// <summary>
    /// Immutable presentation facts for the document, including its final empty insertion position.
    /// </summary>
    public IReadOnlyList<AnalysisPosition> Positions { get; init; } = [];

    /// <summary>
    /// Selects a caret line from the captured document without reanalyzing source or making an RPC.
    /// </summary>
    public AnalysisReply At(int line)
    {
        if (Positions.Count == 0)
        {
            return this;
        }

        var position = Positions[Math.Clamp(line, 0, Positions.Count - 1)];
        return this with
        {
            Stack = position.Stack, BeforeInstruction = position.BeforeInstruction, InstructionHelp = position.InstructionHelp,
        };
    }
}
