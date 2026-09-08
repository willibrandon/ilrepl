namespace IlRepl.Tui;

/// <summary>
/// Where the editor's viewport starts.
/// </summary>
/// <param name="Top">The first visible line, counted from one.</param>
/// <param name="Left">The first visible column, counted from zero.</param>
public readonly record struct ViewportOffsets(int Top, int Left);
