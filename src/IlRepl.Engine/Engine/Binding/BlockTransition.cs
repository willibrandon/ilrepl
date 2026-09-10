namespace IlRepl.Engine.Binding;

/// <summary>
/// A validated protected-region transition before its catch type is bound.
/// </summary>
/// <param name="Kind">The new handler or end boundary.</param>
/// <param name="CatchType">The catch type text, or null for other boundaries.</param>
/// <param name="Message">The standard transition note.</param>
internal sealed record BlockTransition(BlockKind Kind, string? CatchType, string Message);
