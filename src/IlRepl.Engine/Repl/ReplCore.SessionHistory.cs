using System.Globalization;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Formats saved source and output for display without submitting instructions or evaluating runtime values.
/// </summary>
public sealed partial class ReplCore
{
    /// <summary>
    /// Renders the saved journal and historical results independently of the execution transcript's scrollback limit.
    /// </summary>
    /// <param name="document">The reopened document whose source and output are already validated.</param>
    /// <returns>The numbered historical transcript, excluding the unsent editor draft.</returns>
    internal static TranscriptLine[] RenderSessionHistory(SessionDocument document)
    {
        if (document.Entries.Length == 0 && document.Cells.Length == 0)
        {
            return [];
        }

        var lines = new List<TranscriptLine>
        {
            TranscriptLine.Of(LineKind.Info, "  saved session history (no code executed)", SpanStyle.Dim),
        };
        var cells = document.Cells.ToDictionary(cell => cell.Number);
        var lastEntries = new Dictionary<int, int>();
        for (var index = 0; index < document.Entries.Length; index++)
        {
            lastEntries[document.Entries[index].Number] = index;
        }

        var shown = new HashSet<int>();
        var commentOpen = false;
        for (var index = 0; index < document.Entries.Length; index++)
        {
            var entry = document.Entries[index];
            cells.TryGetValue(entry.Number, out var cell);
            if (shown.Add(entry.Number)) Header(entry.Number, cell);
            if (entry.Kind == SessionEntryKind.Rejected)
            {
                lines.Add(TranscriptLine.Of(LineKind.Info, "  rejected input (not applied)", SpanStyle.Dim));
            }

            Source(entry.Number, entry.Source);
            if (entry.Kind is SessionEntryKind.Rejected or SessionEntryKind.Reset) commentOpen = false;
            if (entry.Kind == SessionEntryKind.Rollback && entry.Mark is { } mark) commentOpen = mark.InBlockComment;
            if (index == lastEntries[entry.Number] && cell is not null) lines.AddRange(cell.Output);
        }

        foreach (var cell in document.Cells.Where(cell => !shown.Contains(cell.Number)))
        {
            Header(cell.Number, cell);
            Source(cell.Number, cell.Source);
            lines.AddRange(cell.Output);
        }

        lines.Add(TranscriptLine.Of(LineKind.Info, "  end of saved history; no code executed", SpanStyle.Dim));
        return [.. lines];

        void Header(int number, SessionCell? cell)
        {
            var state = cell is null ? "saved input" : cell.Kind + ", " + cell.State;
            lines.Add(TranscriptLine.Of(LineKind.Info,
                "  " + number.ToString(CultureInfo.InvariantCulture) + ": " + state + " (historical)", SpanStyle.Dim));
        }

        void Source(int number, IEnumerable<string> source)
        {
            var prompt = "il[" + number.ToString(CultureInfo.InvariantCulture) + "]> ";
            foreach (var line in source)
            {
                lines.Add(new TranscriptLine(LineKind.Input,
                    [new TranscriptSpan(prompt, SpanStyle.Prompt), .. Tokenizer.Spans(line, ref commentOpen, SpanStyle.Input)]));
            }
        }
    }
}
