namespace IlRepl.Engine.Binding;

/// <summary>
/// One line as the REPL reads it: its kind, and for a command its name and argument.
/// </summary>
/// <param name="Kind">What the line does.</param>
/// <param name="Line">The line, its comments removed.</param>
/// <param name="Command">The command word for <see cref="ReplLineKind.Command"/>, or null.</param>
/// <param name="Argument">The command argument, trimmed, or empty.</param>
public sealed record ReplLineOperation(ReplLineKind Kind, NormalizedLine Line, string? Command, string Argument);
