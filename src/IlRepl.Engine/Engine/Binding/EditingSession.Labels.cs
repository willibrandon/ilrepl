using IlRepl.Protocol;

namespace IlRepl.Engine.Binding;

public sealed partial class EditingSession
{
    /// <summary>
    /// Finds current and future labels by replaying each possible completed reference through shared transitions.
    /// </summary>
    /// <param name="lines">The complete document.</param>
    /// <param name="caretLine">The line containing the reference being completed.</param>
    /// <param name="site">The classifier's replacement range for that reference.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Current and future labels belonging to the caret's body.</returns>
    public IReadOnlySet<string> ForwardLabels(
        IReadOnlyList<string> lines, int caretLine, CompletionSite site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var view = Speculate(lines, caretLine, cancellationToken: cancellationToken);
        if (site.Kind != CompletionSiteKind.Label || caretLine >= lines.Count)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        for (var index = caretLine; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The tolerant replay decides which preceding lines change comment state.
            // Collect both lexical possibilities here, then confirm each candidate below.
            for (var state = 0; state < 2; state++)
            {
                var comment = state != 0;
                if (CilLexer.Classify(lines[index], ref comment, out var text) == SourceLineKind.Text)
                {
                    candidates.UnionWith(InstructionParser.SplitLabels(text).Labels);
                }
            }
        }

        var original = _state;
        var skipped = _skipped.ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _state = original.Clone();
                _skipped.Clear();
                var line = lines[caretLine];
                var completed = line[..site.ReplaceStart] + candidate + line[site.ReplaceEnd..];
                ApplyLine(completed, caretLine);
                SeedLabelReference(completed, candidate, view.InBlockComment);
                if (_state.Body.LabelSpace == view.LabelSpace && _state.Body.Labels.Contains(candidate))
                {
                    result.Add(candidate);
                    continue;
                }

                for (var index = caretLine + 1; index < lines.Count && !_state.Ended; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_state.Cell.LabelSpace != view.LabelSpace && _state.Method?.LabelSpace != view.LabelSpace)
                    {
                        break;
                    }

                    ApplyLine(lines[index], index);
                    if (_state.Body.LabelSpace == view.LabelSpace && _state.Body.Labels.Contains(candidate))
                    {
                        result.Add(candidate);
                        break;
                    }
                }
            }

            return result;
        }
        finally
        {
            _state = original;
            _skipped.Clear();
            _skipped.AddRange(skipped);
        }
    }

    private void SeedLabelReference(string completed, string candidate, bool inBlockComment)
    {
        _state.Body.ReferencedLabels.Add(candidate);
        CilLexer.Classify(completed, ref inBlockComment, out var text);
        var instruction = InstructionParser.SplitLabels(text).Remainder;
        if (instruction.StartsWith("switch", StringComparison.Ordinal) && instruction.Contains('(') && !instruction.EndsWith(')'))
        {
            instruction += ")";
        }

        try
        {
            var syntax = CilSyntaxParser.ParseInstruction(instruction);
            _state.Body.ReferencedLabels.UnionWith(syntax.Operand.Labels);
        }
        catch (ReplException)
        {
            // An unfinished switch still contributes the reference currently being completed.
        }
    }
}
