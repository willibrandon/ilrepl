using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Associates original editable source with the provisional stores it contributed to.
/// </summary>
/// <param name="Text">The original physical source line.</param>
/// <param name="Mark">The provisional boundary after accepting this line.</param>
/// <param name="Editing">Whether the line belongs to an uncommitted edit.</param>
internal sealed record RetainedSessionLine(string Text, SessionMark Mark, bool Editing);
