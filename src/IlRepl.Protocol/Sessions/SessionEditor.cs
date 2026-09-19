using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlRepl.Protocol;

/// <summary>
/// The unsent editor document, kept separately from accepted engine source.
/// </summary>
public sealed record SessionEditor
{
    /// <summary>
    /// The original physical lines in the editor.
    /// </summary>
    public string[] Lines { get; set; } = [];

    /// <summary>
    /// The caret offset within the text joined with line feeds.
    /// </summary>
    public int Caret { get; set; } = 0;

    /// <summary>
    /// The selection anchor offset, equal to the caret when no selection exists.
    /// </summary>
    public int Anchor { get; set; } = 0;

    /// <summary>
    /// The frontend revision of this editor snapshot.
    /// </summary>
    public long Revision { get; set; } = 0;

    /// <summary>
    /// Additive format fields retained when a supported document is saved again.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>
    /// Preserves saved source while appending current startup input and retaining its caret and selection.
    /// </summary>
    /// <param name="input">The editor captured after the user could begin typing.</param>
    /// <returns>The saved editor when untouched, or the combined source with rebased current editing offsets.</returns>
    public SessionEditor WithStartupInput(SessionEditor input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Revision == 0 && string.Join('\n', input.Lines).Length == 0)
        {
            return this;
        }

        var saved = string.Join('\n', Lines);
        var offset = saved.Length == 0 ? 0 : saved.Length + (input.Lines.Length == 0 ? 0 : 1);
        return this with
        {
            Lines = saved.Length == 0 ? input.Lines : [.. Lines, .. input.Lines],
            Caret = offset + input.Caret,
            Anchor = offset + input.Anchor,
            Revision = input.Revision,
        };
    }
}
