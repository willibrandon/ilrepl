using IlRepl.Protocol;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Keeps source presentation facts reusable across caret moves without retaining type or metadata leases.
/// </summary>
/// <param name="Lines">The analyzed source snapshot.</param>
/// <param name="Reply">The shared diagnostics and session identity.</param>
/// <param name="Positions">The incoming stack and instruction flag at each document line.</param>
internal sealed record AnalyzedDocument(IReadOnlyList<string> Lines, AnalysisReply Reply,
    IReadOnlyList<(AnalyzedStack? Stack, bool BeforeInstruction)> Positions)
{
    /// <summary>
    /// Selects the caret location from already computed document facts.
    /// </summary>
    public AnalysisReply At(AnalysisRequest request)
    {
        var position = Positions[Math.Clamp(request.Line, 0, Positions.Count - 1)];
        return Reply with
        {
            DocumentVersion = request.DocumentVersion, Stack = position.Stack, BeforeInstruction = position.BeforeInstruction,
        };
    }
}
