namespace IlRepl.Engine;

/// <summary>
/// What running a cell produced.
/// </summary>
/// <param name="Value">The value left on the stack, boxed, or null.</param>
/// <param name="IsVoid">True when no path returned a value.</param>
/// <param name="Elapsed">How long the compiled method ran.</param>
/// <param name="StandardOutput">Text the cell wrote to <see cref="Console.Out"/>.</param>
/// <param name="StandardError">Text the cell wrote to <see cref="Console.Error"/>.</param>
public sealed record CellResult(object? Value, bool IsVoid, TimeSpan Elapsed, string StandardOutput, string StandardError);
