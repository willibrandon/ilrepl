using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Uses the shared workspace command parser for source submitted directly to an engine.
/// </summary>
public sealed partial class ReplCore
{
    private bool TrySessionAction(NormalizedLine line, out SessionAction? action)
    {
        action = null;
        if (line.Kind != SourceLineKind.Text) return false;
        try { return SessionCommand.TryParse(line.Text, ReferenceActions, out action); }
        catch (ArgumentException exception) { throw new ReplException(exception.Message, exception); }
    }

    private static string UnquotePath(string text)
    {
        try { return SessionCommand.UnquotePath(text); }
        catch (ArgumentException exception) { throw new ReplException(exception.Message, exception); }
    }
}
