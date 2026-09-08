using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Cuts a buffer into the units a submission sends: single lines at the top level, blank lines
/// that run the cell, and brace blocks from their opening line to the line that balances them.
/// Depth is counted from what the engine already has open, a comment line is never a boundary,
/// and a blank line inside a block is not sent at all.
/// </summary>
public static class SubmissionSplitter
{
    /// <summary>
    /// Splits a buffer.
    /// </summary>
    /// <param name="lines">The buffer's lines.</param>
    /// <param name="openDepth">How many closing braces the engine is already waiting for.</param>
    /// <param name="inBlockComment">Whether the engine has a <c>/*</c> open when the buffer starts.</param>
    /// <returns>The units, in order.</returns>
    public static IReadOnlyList<SubmissionUnit> Split(IReadOnlyList<string> lines, int openDepth = 0, bool inBlockComment = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var units = new List<SubmissionUnit>();
        var depth = Math.Max(0, openDepth);
        var comment = inBlockComment;
        int? blockStart = null;
        var sends = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var before = comment;
            var kind = CilLexer.Classify(line, ref comment, out _);
            var scan = BlockBalance.Scan(line, depth, before);
            if (blockStart is null && depth == 0)
            {
                if (kind == SourceLineKind.Blank)
                {
                    units.Add(new SubmissionUnit(i, i + 1, [i], SubmissionUnitKind.Run));
                    continue;
                }

                if (kind == SourceLineKind.Comment)
                {
                    units.Add(new SubmissionUnit(i, i + 1, [i], SubmissionUnitKind.Line));
                    continue;
                }

                if (scan.Depth > 0)
                {
                    depth = scan.Depth;
                    blockStart = i;
                    sends = [i];
                    continue;
                }

                units.Add(new SubmissionUnit(i, i + 1, [i], SubmissionUnitKind.Line));
                continue;
            }

            blockStart ??= i;
            if (kind == SourceLineKind.Blank)
            {
                continue;
            }

            sends.Add(i);
            if (kind == SourceLineKind.Text)
            {
                depth = scan.Depth;
            }

            if (depth <= 0)
            {
                units.Add(new SubmissionUnit(blockStart.Value, i + 1, sends, SubmissionUnitKind.Block));
                blockStart = null;
                sends = [];
                depth = 0;
            }
        }

        if (blockStart is int open)
        {
            units.Add(new SubmissionUnit(open, lines.Count, sends, SubmissionUnitKind.Block));
        }

        return units;
    }
}
