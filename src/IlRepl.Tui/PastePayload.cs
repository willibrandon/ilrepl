namespace IlRepl.Tui;

/// <summary>
/// What a paste puts in the buffer: the payload with its line endings folded and exactly one
/// trailing newline, the clipboard's terminator, taken off. Every other blank line is the user's.
/// </summary>
public static class PastePayload
{
    /// <summary>
    /// Prepares a payload for insertion.
    /// </summary>
    /// <param name="payload">The pasted text.</param>
    /// <returns>The text to insert.</returns>
    public static string Prepare(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var text = payload.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return text.EndsWith('\n') ? text[..^1] : text;
    }
}
