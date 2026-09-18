using System.Text;
using IlRepl.Engine;
using IlRepl.Engine.Binding;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Retains recallable cell source separately from the full journal of accepted and withdrawn input.
/// </summary>
public sealed partial class ReplCore
{
    private readonly List<RetainedSessionLine> _retainedSource = [];
    private bool _restoringSource;

    /// <summary>
    /// Recalls source with missing persistent inputs while preserving declarations local to the cell's definitions.
    /// </summary>
    /// <param name="cell">The historical cell to edit in the current session.</param>
    /// <returns>Source that can be submitted without redeclaring active arguments, locals, or generic parameters.</returns>
    internal string[] RecallSessionCell(SessionCell cell)
    {
        var argumentNames = Session.Cell.Arguments.Where(argument => argument.Name is not null)
            .Select(argument => argument.Name!).ToHashSet(StringComparer.Ordinal);
        var localNames = Session.Cell.Locals.Where(local => local.Name is not null)
            .Select(local => local.Name!).ToHashSet(StringComparer.Ordinal);
        var parameterNames = Session.TypeParameterNames.ToHashSet(StringComparer.Ordinal);
        var arguments = Session.Cell.Arguments.Count;
        var locals = Session.Cell.Locals.Count;
        var historicalArguments = 0;
        var historicalLocals = 0;
        var vararg = Session.Cell.IsVarArg;
        var typeArguments = NormalizeDeclaration(_typeArgumentsSource);
        var result = new List<string>();
        var comment = false;
        var definition = false;
        var supplied = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in cell.Source)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _ = CilLexer.Classify(line, ref comment, out var text);
            var directive = text.Split([' ', '\t', '('], 2)[0];
            if (directive is ".method" or ".class" or ".edit") break;
            if (directive is ".args" or ".locals" or ".typeparams" or ".vararg" or ".typeargs")
            {
                supplied[text] = supplied.GetValueOrDefault(text) + 1;
            }
        }

        comment = false;
        var missing = new List<string>();
        foreach (var line in cell.Inputs.Reverse().Select(line => NormalizeDeclaration(line)!))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (supplied.GetValueOrDefault(line) is > 0 and var count) supplied[line] = count - 1;
            else missing.Add(line);
        }

        missing.Reverse();
        foreach (var line in missing.Concat(cell.Source))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var previousComment = comment;
            _ = CilLexer.Classify(line, ref comment, out var text);
            var directive = text.Split([' ', '\t', '('], 2)[0];
            if (directive is ".method" or ".class" or ".edit")
            {
                definition = true;
            }

            if (!definition)
            {
                if (directive is ".args" or ".locals" or ".typeparams" or ".vararg")
                {
                    var declaration = MissingDeclaration(directive, text);
                    var recalled = declaration == text ? line : PreserveComments(line, declaration, previousComment);
                    if (recalled is not null) result.Add(recalled);
                    continue;
                }
                else if (directive == ".typeargs")
                {
                    if (text == typeArguments)
                    {
                        if (PreserveComments(line, null, previousComment) is { } recalled) result.Add(recalled);
                        continue;
                    }
                    typeArguments = text;
                }
            }

            result.Add(line);
        }

        return [.. result];

        static string? PreserveComments(string line, string? replacement, bool inComment)
        {
            var rewritten = new StringBuilder();
            var inserted = false;
            var retainedComment = false;
            foreach (var segment in CilLexer.Segments(line, ref inComment))
            {
                var part = line.Substring(segment.Start, segment.Length);
                if (segment.Kind is CilSegmentKind.BlockComment or CilSegmentKind.LineComment)
                {
                    rewritten.Append(part);
                    retainedComment = true;
                }
                else if (string.IsNullOrWhiteSpace(part)) rewritten.Append(part);
                else if (!inserted)
                {
                    rewritten.Append(replacement);
                    inserted = true;
                }
            }

            return retainedComment || replacement is not null ? rewritten.ToString() : null;
        }

        string? MissingDeclaration(string directive, string text)
        {
            if (directive == ".vararg")
            {
                if (vararg) return null;
                vararg = true;
                return text;
            }

            var spec = text[directive.Length..];
            if (directive == ".typeparams")
            {
                var names = CellGenericBinding.Parameters(spec, [], true);
                var added = names.Where(parameterNames.Add).ToArray();
                return added.Length == 0 ? null : added.Length == names.Length ? text : ".typeparams (" + string.Join(", ", added) + ")";
            }

            var local = directive == ".locals";
            spec = TypeParser.Normalize(spec).Trim();
            var position = 0;
            var init = local && TypeParser.TryKeyword(spec, ref position, "init");
            spec = spec[position..].Trim();
            if (spec.StartsWith('(') && spec.EndsWith(')')) spec = spec[1..^1].Trim();
            var parts = CilSyntaxParser.SplitTopLevel(spec);
            var retained = new List<string>();
            foreach (var part in parts)
            {
                var declaration = part;
                if (local && declaration.StartsWith('[') && declaration.IndexOf(']', StringComparison.Ordinal) is var close
                    && close > 0 && int.TryParse(declaration.AsSpan(1, close - 1), out _))
                {
                    declaration = declaration[(close + 1)..].Trim();
                }

                if (!local && DeclarationText.InitializerEquals(declaration) is var equals && equals >= 0)
                {
                    declaration = declaration[..equals].Trim();
                }

                position = 0;
                _ = CilSyntaxParser.ParseTypeAt(declaration, ref position);
                var name = InstructionParser.Unquote(declaration[position..].Trim());
                var slot = local ? historicalLocals++ : historicalArguments++;
                var added = name.Length == 0 ? slot >= (local ? locals : arguments)
                    : (local ? localNames : argumentNames).Add(name);
                if (!added) continue;
                retained.Add(part);
                if (local) locals++;
                else arguments++;
            }

            return retained.Count == 0 ? null : retained.Count == parts.Count ? text
                : directive + (init ? " init (" : " (") + string.Join(", ", retained) + ")";
        }

        static string? NormalizeDeclaration(string? line)
        {
            if (line is null) return null;
            var inComment = false;
            _ = CilLexer.Classify(line, ref inComment, out var text);
            return text;
        }
    }

    private string[] TrackSource(SessionEntry entry, int previousNumber, bool editing, bool commentBefore = false)
    {
        var mark = Session.Mark();
        if (entry.Kind == SessionEntryKind.Reset)
        {
            _retainedSource.Clear();
        }
        else if (entry.Kind is SessionEntryKind.Clear or SessionEntryKind.Undo or SessionEntryKind.Rollback)
        {
            _retainedSource.RemoveAll(line => line.Mark.BodyLines > mark.BodyLines
                || line.Mark.DeclarationLines > mark.DeclarationLines
                || (line.Mark.OpenMethodLines is { } method && (mark.OpenMethodLines is null || method > mark.OpenMethodLines))
                || (line.Mark.OpenMethodLines is not null && line.Mark.BraceSeen && !mark.BraceSeen)
                || (line.Mark.OpenTypeLines is { } type && (mark.OpenTypeLines is null || type > mark.OpenTypeLines))
                || (line.Editing && _editBlock is null));
        }
        else if (entry.Kind is SessionEntryKind.Source or SessionEntryKind.Edit or SessionEntryKind.EditSource or SessionEntryKind.Run)
        {
            _retainedSource.AddRange(entry.Source.Select(line => new RetainedSessionLine(line, mark, editing)));
        }
        else if (entry.Kind == SessionEntryKind.Rejected && commentBefore && _retainedSource.Count != 0 && entry.Source.Length != 0)
        {
            var raw = entry.Source[0];
            var comment = true;
            var segments = CilLexer.Segments(raw, ref comment);
            if (segments.Count != 0 && raw[..segments[0].End].EndsWith("*/", StringComparison.Ordinal))
            {
                _retainedSource.Add(new RetainedSessionLine(raw[..segments[0].End], mark, editing));
            }
        }

        if (CellNumber == previousNumber)
        {
            return [];
        }

        var result = _retainedSource.Select(line => line.Text).ToArray();
        _retainedSource.Clear();
        return result;
    }

    private void RestoreTrackedEntry(SessionEntry entry)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (entry.Source.Length > 1 && entry.Kind is SessionEntryKind.Source or SessionEntryKind.EditSource or SessionEntryKind.Rejected)
        {
            foreach (var line in entry.Source)
            {
                RestoreTrackedEntry(entry with { Source = [line] });
            }

            return;
        }

        var number = CellNumber;
        var editing = _editBlock is not null || entry.Kind == SessionEntryKind.Edit;
        var commentBefore = Session.InBlockComment;
        var restoring = _restoringSource;
        _restoringSource = true;
        try
        {
            RestoreEntry(entry);
        }
        finally
        {
            _restoringSource = restoring;
        }

        _ = TrackSource(entry, number, editing, commentBefore);
    }
}
