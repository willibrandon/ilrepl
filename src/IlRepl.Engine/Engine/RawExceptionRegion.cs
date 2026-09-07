namespace IlRepl.Engine;

/// <summary>
/// An exception handling clause as the body header encodes it, before any type resolves.
/// </summary>
/// <param name="Kind">The clause kind.</param>
/// <param name="TryOffset">The start of the protected range.</param>
/// <param name="TryLength">The length of the protected range.</param>
/// <param name="HandlerOffset">The start of the handler.</param>
/// <param name="HandlerLength">The length of the handler.</param>
/// <param name="FilterOffset">The start of the filter code, for a filter clause.</param>
/// <param name="CatchToken">The catch type token, or 0.</param>
/// <param name="CatchType">The catch type when the reflection path already resolved it; null otherwise.</param>
public sealed record RawExceptionRegion(IlClauseKind Kind, int TryOffset, int TryLength, int HandlerOffset, int HandlerLength, int FilterOffset, int CatchToken, Type? CatchType);
