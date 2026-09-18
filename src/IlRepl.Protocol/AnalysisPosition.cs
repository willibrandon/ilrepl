namespace IlRepl.Protocol;

/// <summary>
/// Holds caret presentation for one analyzed document line without requiring another engine request.
/// </summary>
/// <param name="Stack">The incoming stack, or null outside an executable body.</param>
/// <param name="BeforeInstruction">Whether the line begins an instruction.</param>
/// <param name="InstructionHelp">The instruction explanation for this position.</param>
public sealed record AnalysisPosition(AnalyzedStack? Stack, bool BeforeInstruction, InstructionHelp? InstructionHelp);
