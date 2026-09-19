namespace IlRepl.Tui;

/// <summary>
/// What a paste puts in the buffer: the payload with its line endings folded and exactly one trailing newline taken off.
/// </summary>
/// <remarks>
/// The trailing newline taken off is the clipboard's terminator. Every other blank line is the user's.
/// </remarks>
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
