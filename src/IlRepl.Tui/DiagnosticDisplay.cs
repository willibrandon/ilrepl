using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Retains a diagnostic's presentation without retaining actionable source locations or stack state.
/// </summary>
/// <param name="Text">The explanation displayed above the prompt.</param>
/// <param name="Style">The explanation's colour.</param>
public sealed record DiagnosticDisplay(string Text, SpanStyle Style);
