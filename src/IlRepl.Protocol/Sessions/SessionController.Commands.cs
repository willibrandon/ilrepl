namespace IlRepl.Protocol;

/// <summary>
/// Keeps workspace commands available when the execution host is starting or unavailable.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>
    /// Identifies commands that the frontend can safely process without an available execution runtime.
    /// </summary>
    /// <param name="text">The complete submitted editor text.</param>
    /// <returns>Whether the command can run while source remains locally retained.</returns>
    public bool CanHandleWithoutRuntime(string text)
    {
        if (text.Contains('\n')) return false;
        if (text.Trim() is ".help" or ".h" or "?") return true;
        try
        {
            return FrontendAction(text)?.Operation is SessionOperation.Quit or SessionOperation.Restart
                or SessionOperation.Save or SessionOperation.Capture or SessionOperation.Summary
                or SessionOperation.Cells or SessionOperation.Cell;
        }
        catch (ArgumentException) { return false; }
    }

    private SessionAction? FrontendAction(string line)
    {
        var comment = Status.Mark.InBlockComment;
        if (CilLexer.Classify(line, ref comment, out var text) != SourceLineKind.Text) return null;
        if (text is ".quit" or ".exit" or ".q") return new SessionAction { Operation = SessionOperation.Quit };
        return SessionCommand.TryParse(text, _engine is IHostedEngine, out var action) ? action : null;
    }

    private TranscriptLine CommandEcho(string line) => new(LineKind.Input,
        [new TranscriptSpan(Status.Prompt, SpanStyle.Prompt), new TranscriptSpan(line, SpanStyle.Input)]);

    private HandleReply LocalHelp() => new(true, false,
        [.. Catalog.Where(item => item.Name.StartsWith('.')).Select(item =>
            TranscriptLine.Of(LineKind.Info, "  " + item.Name + "  " + item.Description, SpanStyle.Dim))], Status);

    private async Task<SessionReply> PerformOfflineAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        var captured = await CaptureAsync(cancellationToken).ConfigureAwait(false);
        var action = request.Action;
        if (action.Operation == SessionOperation.Save)
        {
            var path = await SessionSnapshotStore.WriteAsync(action.Path!, captured.Document, action.Embed, cancellationToken)
                .ConfigureAwait(false);
            return captured with { Path = path, Dirty = false, Reply = new HandleReply(true, false,
                [TranscriptLine.Of(LineKind.Info, "  saved session " + path, SpanStyle.Dim)], Status) };
        }
        if (action.Operation is SessionOperation.Capture or SessionOperation.Summary)
        {
            return captured with { Reply = new HandleReply(true, false,
                [TranscriptLine.Of(LineKind.Info, "  session: " + (captured.Path ?? "unsaved scratch")
                    + "; execution host unavailable", SpanStyle.Dim)], Status) };
        }
        if (action.Operation == SessionOperation.Cells)
        {
            return captured with { Reply = new HandleReply(true, false,
                [.. captured.Document.Cells.SelectMany(cell => new[]
                {
                    TranscriptLine.Of(LineKind.Info, $"  {cell.Number}: {cell.Kind}, {cell.State} (historical)", SpanStyle.Dim),
                }.Concat(cell.Output))], Status) };
        }
        if (action.Operation == SessionOperation.Cell)
        {
            var number = action.Numbers.Single();
            var cell = captured.Document.Cells.FirstOrDefault(cell => cell.Number == number)
                ?? throw new InvalidOperationException($"no retained cell {number}; use .session cells");
            var lines = cell.Kind == "cell" ? cell.Inputs.Concat(cell.Source).ToArray() : cell.Source;
            var length = string.Join('\n', lines).Length;
            var editor = new SessionEditor { Lines = lines, Caret = length, Anchor = length, Revision = Editor.Revision + 1 };
            return captured with { Document = captured.Document with { Editor = editor }, Reply = new HandleReply(true, false,
                [TranscriptLine.Of(LineKind.Info, $"  recalled cell {number}; previous output is historical", SpanStyle.Dim),
                    .. cell.Output], Status) { SessionEditor = editor } };
        }
        return captured with { Reply = Failure("host unavailable; use .session restart before this operation") };
    }
}
