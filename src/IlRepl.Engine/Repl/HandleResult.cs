namespace IlRepl.Repl;

/// <summary>
/// What happened when the REPL handled a line.
/// </summary>
/// <param name="Succeeded">False when the line produced an error.</param>
/// <param name="QuitRequested">True when the line asked to leave.</param>
public sealed record HandleResult(bool Succeeded, bool QuitRequested);
