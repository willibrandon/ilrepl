using Hex1b.Documents;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Translates selected generic definitions through edits and drops selections whose defining text changes.
/// </summary>
public sealed class ContinuationAnchors : IDisposable
{
    private readonly IHex1bDocument _document;
    private readonly List<AnchoredCompletion> _anchors = [];

    /// <summary>
    /// Changes when a selected definition is added, cleared or translated through an edit.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>
    /// Observes edits on the prompt's document.
    /// </summary>
    /// <param name="document">The document whose generic owners are anchored.</param>
    public ContinuationAnchors(IHex1bDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _document.Changed += Changed;
    }

    /// <summary>
    /// Retains an accepted owner span, including its opening angle bracket.
    /// </summary>
    /// <param name="start">The absolute start offset.</param>
    /// <param name="end">The absolute end offset.</param>
    /// <param name="token">The host token.</param>
    public void Add(int start, int end, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (start < 0 || end <= start || end > _document.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        var text = _document.GetText(new DocumentRange(new DocumentOffset(start), new DocumentOffset(end)));
        _anchors.RemoveAll(anchor => anchor.Start == start);
        _anchors.Add(new AnchoredCompletion(start, end, token, text));
        Version++;
    }

    /// <summary>
    /// Converts the current valid owner spans to the protocol's line-relative coordinates.
    /// </summary>
    /// <returns>The structurally comparable anchor list.</returns>
    public IReadOnlyList<ContinuationAnchor> Snapshot()
    {
        var result = new List<ContinuationAnchor>();
        foreach (var anchor in _anchors)
        {
            if (anchor.Start < 0 || anchor.End > _document.Length)
            {
                continue;
            }

            var range = new DocumentRange(new DocumentOffset(anchor.Start), new DocumentOffset(anchor.End));
            if (_document.GetText(range) != anchor.Text)
            {
                continue;
            }

            var start = _document.OffsetToPosition(range.Start);
            var end = _document.OffsetToPosition(range.End);
            if (start.Line == end.Line)
            {
                result.Add(new ContinuationAnchor(start.Line - 1, start.Column - 1, end.Column - 1, anchor.Token));
            }
        }

        return result.OrderBy(anchor => anchor.Line).ThenBy(anchor => anchor.Start).ToArray();
    }

    /// <summary>
    /// Releases every selected definition after a live session change or document replacement.
    /// </summary>
    public void Clear()
    {
        if (_anchors.Count > 0)
        {
            _anchors.Clear();
            Version++;
        }
    }

    /// <summary>
    /// Stops observing the document and releases its retained selections.
    /// </summary>
    public void Dispose()
    {
        _document.Changed -= Changed;
        Clear();
    }

    private void Changed(object? sender, DocumentChangedEventArgs change)
    {
        Version++;
        foreach (var operation in change.Operations)
        {
            var (start, end, inserted) = operation switch
            {
                InsertOperation insert => (insert.Offset.Value, insert.Offset.Value, insert.Text.Length),
                DeleteOperation delete => (delete.Range.Start.Value, delete.Range.End.Value, 0),
                ReplaceOperation replace => (replace.Range.Start.Value, replace.Range.End.Value, replace.NewText.Length),
                _ => (-1, -1, 0),
            };
            if (start < 0)
            {
                Clear();
                return;
            }

            for (var index = _anchors.Count - 1; index >= 0; index--)
            {
                var anchor = _anchors[index];
                if (end <= anchor.Start)
                {
                    var delta = inserted - (end - start);
                    _anchors[index] = anchor with { Start = anchor.Start + delta, End = anchor.End + delta };
                }
                else if (start < anchor.End)
                {
                    _anchors.RemoveAt(index);
                }
            }
        }
    }
}
