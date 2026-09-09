namespace IlRepl.Protocol;

/// <summary>
/// A selected generic definition retained while its arguments are edited.
/// </summary>
/// <param name="Line">The zero-based document line.</param>
/// <param name="Start">The start of the defining span, in UTF-16 code units.</param>
/// <param name="End">The end of the defining span.</param>
/// <param name="Token">The host's opaque definition token.</param>
public sealed record ContinuationAnchor(int Line, int Start, int End, string Token);
