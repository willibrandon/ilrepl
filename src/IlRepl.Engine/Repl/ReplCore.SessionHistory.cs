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
    internal static TranscriptLine[] RenderSessionHistory(SessionDocument document) => RenderSessionHistoryTail(document, 0);

    /// <summary>
    /// Renders the exact requested history tail while preserving lexical state across undisplayed source.
    /// </summary>
    /// <param name="document">The complete validated source and output journal.</param>
    /// <param name="maximumLines">The retained presentation rows, or zero for unlimited history.</param>
    /// <returns>The same styled rows as the unlimited history followed by TakeLast, without materializing its hidden prefix.</returns>
    internal static TranscriptLine[] RenderSessionHistoryTail(SessionDocument document, int maximumLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLines);
        if (document.Entries.Length == 0 && document.Cells.Length == 0)
        {
            return [];
        }

        var lines = new List<TranscriptLine>();
        var cells = document.Cells.ToDictionary(cell => cell.Number);
        var lastEntries = new Dictionary<int, int>();
        for (var index = 0; index < document.Entries.Length; index++)
        {
            lastEntries[document.Entries[index].Number] = index;
        }

        var remaining = maximumLines == 0 ? 0L : Math.Max(0L, 2L + lastEntries.Count
            + document.Entries.Sum(entry => (long)entry.Source.Length + (entry.Kind == SessionEntryKind.Rejected ? 1 : 0))
            + document.Cells.Sum(cell => (long)cell.Output.Length
                + (lastEntries.ContainsKey(cell.Number) ? 0 : 1L + cell.Source.Length)) - maximumLines);
        if (KeepRow())
        {
            lines.Add(TranscriptLine.Of(LineKind.Info, "  saved session history (no code executed)", SpanStyle.Dim));
        }

        var shown = new HashSet<int>();
        var commentOpen = false;
        for (var index = 0; index < document.Entries.Length; index++)
        {
            var entry = document.Entries[index];
            cells.TryGetValue(entry.Number, out var cell);
            if (shown.Add(entry.Number))
            {
                Header(entry.Number, cell);
            }

            if (entry.Kind == SessionEntryKind.Rejected && KeepRow())
            {
                lines.Add(TranscriptLine.Of(LineKind.Info, "  rejected input (not applied)", SpanStyle.Dim));
            }

            Source(entry.Number, entry.Source);
            if (entry.Kind is SessionEntryKind.Rejected or SessionEntryKind.Reset)
            {
                commentOpen = false;
            }

            if (entry.Kind == SessionEntryKind.Rollback && entry.Mark is { } mark)
            {
                commentOpen = mark.InBlockComment;
            }

            if (index == lastEntries[entry.Number] && cell is not null)
            {
                Output(cell.Output);
            }
        }

        foreach (var cell in document.Cells.Where(cell => !shown.Contains(cell.Number)))
        {
            Header(cell.Number, cell);
            Source(cell.Number, cell.Source);
            Output(cell.Output);
        }

        lines.Add(TranscriptLine.Of(LineKind.Info, "  end of saved history; no code executed", SpanStyle.Dim));
        return [.. lines];

        void Header(int number, SessionCell? cell)
        {
            if (!KeepRow())
            {
                return;
            }

            var state = cell is null ? "saved input" : cell.Kind + ", " + cell.State;
            lines.Add(TranscriptLine.Of(LineKind.Info,
                "  " + number.ToString(CultureInfo.InvariantCulture) + ": " + state + " (historical)", SpanStyle.Dim));
        }

        void Source(int number, IEnumerable<string> source)
        {
            var prompt = "il[" + number.ToString(CultureInfo.InvariantCulture) + "]> ";
            foreach (var line in source)
            {
                if (KeepRow())
                {
                    lines.Add(new TranscriptLine(LineKind.Input,
                        [new TranscriptSpan(prompt, SpanStyle.Prompt), .. Tokenizer.Spans(line, ref commentOpen, SpanStyle.Input)]));
                }
                else
                {
                    _ = CilLexer.Segments(line, ref commentOpen);
                }
            }
        }

        void Output(IEnumerable<TranscriptLine> output)
        {
            foreach (var line in output)
            {
                if (KeepRow())
                {
                    lines.Add(line);
                }
            }
        }

        bool KeepRow()
        {
            if (remaining == 0)
            {
                return true;
            }

            remaining--;
            return false;
        }
    }
}
