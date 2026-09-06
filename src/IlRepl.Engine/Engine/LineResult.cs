namespace IlRepl.Engine;

/// <summary>
/// The result of adding a line to a cell.
/// </summary>
/// <param name="Outcome">What the line was.</param>
/// <param name="Instruction">The instruction, when <paramref name="Outcome"/> is <see cref="LineOutcome.Instruction"/>.</param>
/// <param name="Message">An optional note for the transcript, such as a block or declaration summary.</param>
public sealed record LineResult(LineOutcome Outcome, Instruction? Instruction, string? Message);
