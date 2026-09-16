using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// An actionable source location or documentation URL in contextual help.
/// </summary>
/// <param name="Label">The visible target.</param>
/// <param name="Source">The source to navigate to, when available in the current document.</param>
/// <param name="Url">The documentation URL, when this action opens documentation.</param>
public sealed record HelpAction(string Label, AnalysisSource? Source = null, string? Url = null);
