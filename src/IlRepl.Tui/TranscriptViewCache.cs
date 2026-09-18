using Hex1b;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Reuses transcript widgets and folded rows until retained content or its presentation changes.
/// </summary>
internal sealed class TranscriptViewCache
{
    private readonly Dictionary<TranscriptLine, TranscriptLineWidget> _lines = new(ReferenceEqualityComparer.Instance);
    private Transcript? _transcript;
    private long _version = -1;
    private int _width;
    private int _flashTop;
    private int _flashBottom;
    private Hex1bWidget[] _widgets = [];

    /// <summary>
    /// Returns retained line widgets while leaving selection and viewport behavior to their live parent widgets.
    /// </summary>
    internal Hex1bWidget[] Get(Transcript transcript, int width, YankFeedback feedback)
    {
        if (ReferenceEquals(transcript, _transcript) && _version == transcript.Version && _width == width
            && _flashTop == feedback.FlashTop && _flashBottom == feedback.FlashBottom)
        {
            return _widgets;
        }

        if (!ReferenceEquals(transcript, _transcript) || width != _width)
        {
            _lines.Clear();
        }

        _transcript = transcript;
        _version = transcript.Version;
        _width = width;
        _flashTop = feedback.FlashTop;
        _flashBottom = feedback.FlashBottom;
        var retained = new HashSet<TranscriptLine>(ReferenceEqualityComparer.Instance);
        var row = 0;
        _widgets = new Hex1bWidget[transcript.Lines.Count];
        for (var index = 0; index < transcript.Lines.Count; index++)
        {
            var line = transcript.Lines[index];
            retained.Add(line);
            if (!_lines.TryGetValue(line, out var widget))
            {
                widget = new TranscriptLineWidget(line, width).Cached(static _ => true);
            }

            var rows = feedback.FlashTop >= 0 ? widget.Rows.Count : 0;
            var flash = feedback.Covers(row, rows);
            row += rows;
            if (widget.Flash != flash)
            {
                widget = widget with { Flash = flash };
            }

            _lines[line] = widget;
            _widgets[index] = widget;
        }

        foreach (var line in _lines.Keys.Where(line => !retained.Contains(line)).ToArray())
        {
            _lines.Remove(line);
        }

        return _widgets;
    }
}
