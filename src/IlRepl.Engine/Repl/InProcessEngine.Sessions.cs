using System.Runtime.InteropServices;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Captures and reconstructs portable workspaces under the same gate as source submission.
/// </summary>
public sealed partial class InProcessEngine
{
    private string? _sessionPath;
    private string? _savedSessionHash;
    private string[] _sessionDiagnostics = [];
    private Func<SessionRequest, CancellationToken, Task<SessionReply>>? _sessionTooling;

    /// <summary>
    /// Publishes the coherent workspace checkpoint before its serialized session operation releases ownership.
    /// </summary>
    public Action<SessionReply>? WorkspaceCheckpoint { get; set; }

    /// <summary>
    /// Coordinates storage and dependency tooling supplied by the execution host.
    /// </summary>
    public Func<SessionRequest, CancellationToken, Task<SessionReply>>? SessionTooling
    {
        get => _sessionTooling;
        set
        {
            _sessionTooling = value;
            _core.ReferenceActions = value is not null;
        }
    }

    /// <inheritdoc />
    public async Task<SessionReply> SessionAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Action.Operation is SessionOperation.Save or SessionOperation.Open or SessionOperation.Restore or SessionOperation.Load)
        {
            if (SessionTooling is null)
            {
                throw new ReplEngineException(request.Action.Operation is SessionOperation.Load or SessionOperation.Restore
                    ? "load or restore dependencies in terminal ilrepl, save with .session save --embed, then open the file in the demo"
                    : "this environment uses Open and Download controls for session files");
            }

            return await RunOperationAsync(request.Action.Operation.ToString().ToLowerInvariant(), async token =>
            {
                var reply = await SessionTooling(request, token).ConfigureAwait(false);
                WorkspaceCheckpoint?.Invoke(reply);
                return reply;
            }, cancellationToken).ConfigureAwait(false);
        }

        await _warmupCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            return await ExecuteOperationAsync("session", token =>
            {
                var reply = HandleSession(request, token);
                WorkspaceCheckpoint?.Invoke(reply);
                return reply;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ReplException exception)
        {
            throw new ReplEngineException(exception.Message, exception);
        }
    }

    private SessionReply HandleSession(SessionRequest request, CancellationToken cancellationToken) =>
        _core.WithCancellation(() => HandleSessionCore(request, cancellationToken), cancellationToken);

    private SessionReply HandleSessionCore(SessionRequest request, CancellationToken cancellationToken)
    {
        var action = request.Action;
        if (action.Operation == SessionOperation.AdoptReferences)
        {
            _core.AdoptReferences(request.Document ?? throw new ReplException("a verified dependency graph is required"));
            _sessionDiagnostics = [];
            return CaptureReply(request.Editor) with { Reply = Reply(new HandleResult(true, false)) };
        }
        if (action.Operation == SessionOperation.AcknowledgeSave)
        {
            MarkSessionSaved(action.Path ?? throw new ReplException("the saved path is missing"),
                request.Document ?? throw new ReplException("the saved snapshot is missing"));
            return CaptureReply(request.Editor);
        }

        if (action.Operation == SessionOperation.Hydrate)
        {
            var document = request.Document ?? throw new ReplException("no session document was supplied");
            _sessionDiagnostics = _core.ReopenSession(document);
            _sessionPath = action.Path;
            _savedSessionHash = request.Modified ? null : DocumentHash(_core.CaptureSession(document.Editor));
            if (request.AnnounceOpen)
            {
                _core.Transcript.Add(LineKind.Info,
                    "  Session opened. Nothing has run yet. Saved output is shown for reference.", SpanStyle.Dim);
                if (document.Assets.Length != 0)
                {
                    _core.Transcript.Add(LineKind.Info,
                        "  embedded code can run when you explicitly execute the experiment", SpanStyle.Dim);
                }
            }

            foreach (var diagnostic in _sessionDiagnostics)
            {
                _core.Transcript.Add(LineKind.Error, "  " + diagnostic, SpanStyle.Error);
            }

            foreach (var interruption in document.Interruptions)
            {
                _core.Transcript.Add(LineKind.Info,
                    $"  previous attempt starting at prompt {interruption.Number} was interrupted; no code was replayed",
                    SpanStyle.Dim);
            }

            var reply = Reply(new HandleResult(true, false));
            return CaptureReply(document.Editor) with
            {
                Reply = reply with
                {
                    Lines = [.. reply.Lines, .. ReplCore.RenderSessionHistory(document)],
                    SessionEditor = document.Editor,
                },
            };
        }

        if (action.Operation == SessionOperation.Run)
        {
            var document = request.Document ?? throw new ReplException("explicit session replay requires a source document");
            var result = _core.RunSession(document, action.Numbers, cancellationToken);
            _sessionPath = action.Path;
            return CaptureReply(new SessionEditor()) with
            {
                Reply = Reply(result) with { SessionEditor = new SessionEditor() },
            };
        }

        if (action.Operation == SessionOperation.Cell)
        {
            var number = action.Numbers.Single();
            var cell = _core.CaptureSession(request.Editor).Cells.SingleOrDefault(cell => cell.Number == number)
                ?? throw new ReplException($"no retained cell {number}; use .session cells");
            var lines = _core.RecallSessionCell(cell);
            var editor = new SessionEditor { Lines = lines, Caret = string.Join('\n', lines).Length,
                Anchor = string.Join('\n', lines).Length, Revision = request.Editor.Revision + 1 };
            _core.Transcript.Add(LineKind.Info, $"  recalled cell {number}; previous output is historical", SpanStyle.Dim);
            foreach (var output in cell.Output)
            {
                _core.Transcript.Add(output);
            }

            return CaptureReply(editor) with { Reply = Reply(new HandleResult(true, false)) with { SessionEditor = editor } };
        }

        if (action.Operation == SessionOperation.Cells)
        {
            var cells = _core.CaptureSession(request.Editor).Cells;
            if (cells.Length == 0)
            {
                _core.Transcript.Add(LineKind.Info, "  no retained cells; the current input has not completed a submission",
                    SpanStyle.Dim);
            }

            foreach (var cell in cells)
            {
                _core.Transcript.Add(LineKind.Info, $"  {cell.Number}: {cell.Kind}, {cell.State} (historical)", SpanStyle.Dim);
                foreach (var line in cell.Output)
                {
                    _core.Transcript.Add(line);
                }
            }
        }
        else if (action.Operation == SessionOperation.Summary)
        {
            var captured = _core.CaptureSession(request.Editor);
            var dirty = IsDirty(captured);
            _core.Transcript.Add(LineKind.Info, "  session: " + (_sessionPath ?? "unsaved scratch")
                + (dirty ? " (modified)" : ""), SpanStyle.Dim);
            _core.Transcript.Add(LineKind.Info, "  recorded runtime: " + captured.Runtime.Description + ", " + captured.Runtime.Rid,
                SpanStyle.Dim);
            _core.Transcript.Add(LineKind.Info, "  current runtime: " + RuntimeInformation.FrameworkDescription + ", "
                + RuntimeInformation.RuntimeIdentifier, SpanStyle.Dim);
            _core.Transcript.Add(LineKind.Info,
                "  reopening retains source and historical results; objects and static field values are not restored", SpanStyle.Dim);
            if (captured.Assets.Length != 0)
            {
                _core.Transcript.Add(LineKind.Info, "  embedded code can run on explicit execution", SpanStyle.Dim);
            }

            foreach (var reference in captured.References.Where(reference => reference.Origin != "baseline"))
            {
                _core.Transcript.Add(LineKind.Info, "  " + reference.Origin + ": " + reference.Request
                    + (reference.Version is { } version ? " " + version : "")
                    + " (" + _core.ReferenceStatus(reference) + ")", SpanStyle.Dim);
            }

            foreach (var diagnostic in _sessionDiagnostics)
            {
                _core.Transcript.Add(LineKind.Error, "  " + diagnostic, SpanStyle.Error);
            }
        }

        return CaptureReply(request.Editor) with { Reply = Reply(new HandleResult(true, false)) };
    }

    /// <summary>
    /// Marks only the captured revision as saved after the host finishes its atomic write.
    /// </summary>
    /// <param name="path">The associated full path.</param>
    /// <param name="document">The snapshot actually written.</param>
    internal void MarkSessionSaved(string path, SessionDocument document)
    {
        _sessionPath = path;
        _savedSessionHash = DocumentHash(document);
    }

    private SessionReply CaptureReply(SessionEditor editor)
    {
        var document = _core.CaptureSession(editor);
        return new SessionReply { Document = document, Path = _sessionPath, Dirty = IsDirty(document),
            Diagnostics = _sessionDiagnostics, Reply = new HandleReply(true, false, [], _core.Status) };
    }

    private bool IsDirty(SessionDocument document) => _savedSessionHash is null
        ? document.Entries.Length != 0 || document.Editor.Lines.Any(line => line.Length != 0)
        : _savedSessionHash != DocumentHash(document);

    private static string DocumentHash(SessionDocument document) => SessionCodec.Hash(SessionCodec.Write(document with
    {
        Editor = document.Editor with { Caret = 0, Anchor = 0, Revision = 0,
            Lines = document.Editor.Lines.Length == 1 && document.Editor.Lines[0].Length == 0 ? [] : document.Editor.Lines },
        Assets = [],
    }));
}
