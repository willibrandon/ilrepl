namespace IlRepl.Engine.Binding;

/// <summary>
/// An unsent line whose failure was isolated while later lines continued to be inspected.
/// </summary>
/// <param name="Line">The zero-based document line.</param>
/// <param name="Text">The line as typed.</param>
/// <param name="Message">The recoverable binding error.</param>
public sealed record SkippedEditingLine(int Line, string Text, string Message);
