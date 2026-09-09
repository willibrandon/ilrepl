using System.Collections.Concurrent;
using Hex1b.Documents;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// What the prompt keeps between frames: the editor and its document, the history, the palette's
/// selection, the submission in flight, and the queue other threads post to. It lives above the
/// widget so the status bar, the frame's drain, and the key bindings all see the same thing.
/// </summary>
public sealed partial class PromptState
{
    /// <summary>
    /// Initializes the state.
    /// </summary>
    /// <param name="history">The history.</param>
    /// <param name="tokenizer">The tokenizer that colours the buffer.</param>
    public PromptState(PromptHistory history, CilTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(tokenizer);
        History = history;
        Editor = new EditorState(new Hex1bDocument("")) { TabSize = AutoIndent.Unit.Length };
        Highlighter = new CilDecorationProvider(tokenizer);
        Commands = tokenizer.Vocabulary.Commands;
        Prediction = new PredictionHint();
        View = new PromptView();
        View.Prediction = Prediction;
        Anchors = new ContinuationAnchors(Editor.Document);
        Editor.Document.Changed += (_, _) => CompletionTextChanged();
    }

    /// <summary>
    /// The editor: the document, the caret, the selection, and the undo history.
    /// </summary>
    public EditorState Editor { get; }

    /// <summary>
    /// The history Up and Down walk.
    /// </summary>
    public PromptHistory History { get; }

    /// <summary>
    /// Colours the buffer.
    /// </summary>
    public CilDecorationProvider Highlighter { get; }

    /// <summary>
    /// The dot-words that are commands, whose arguments hold no brace that counts.
    /// </summary>
    public IReadOnlyList<string> Commands { get; }

    /// <summary>
    /// The ghost text after the caret.
    /// </summary>
    public PredictionHint Prediction { get; }

    /// <summary>
    /// Renders the buffer with the prompt in its gutter and keeps the caret in view.
    /// </summary>
    public PromptView View { get; }

    /// <summary>
    /// Events other threads posted, drained at the start of every frame.
    /// </summary>
    public ConcurrentQueue<SubmissionEvent> Events { get; } = new();

    /// <summary>
    /// Asks for a frame; set by the app.
    /// </summary>
    public Action? Invalidate { get; set; }

    /// <summary>
    /// The palette row the user has moved to.
    /// </summary>
    public int SelectedIndex { get; set; }

    /// <summary>
    /// Whether Escape closed the palette for the word being typed.
    /// </summary>
    public bool PaletteDismissed
    {
        get => Palette == PaletteMode.Dismissed;
        set
        {
            if (value)
            {
                Palette = PaletteMode.Dismissed;
                DismissedVersion = Editor.Document.Version;
                DismissedRevision = Requester?.Revision ?? -1;
                DismissedCaret = (CaretLine - 1, CaretColumn);
            }
            else if (Palette == PaletteMode.Dismissed)
            {
                Palette = PaletteMode.Closed;
            }
        }
    }

    /// <summary>
    /// Whether the user moved the palette's highlight since the word changed; only then does Enter accept.
    /// </summary>
    public bool PaletteNavigated { get; set; }

    /// <summary>
    /// The submission in flight, or null.
    /// </summary>
    public Submission? Submission { get; set; }

    /// <summary>
    /// Buffers submitted while another submission was in flight, oldest first; each goes when
    /// the one before it completes.
    /// </summary>
    public Queue<string> Pending { get; } = new();

    /// <summary>
    /// Whether the store's problem has been printed.
    /// </summary>
    public bool HistoryProblemShown { get; set; }

    /// <summary>
    /// The document's length after the last text change, for telling a single typed character apart.
    /// </summary>
    public int LastLength { get; set; }

    /// <summary>
    /// Whether a submission is in flight: from Enter until the frame that drains its last event.
    /// </summary>
    public bool Busy => Submission is not null;

    /// <summary>
    /// The buffer.
    /// </summary>
    public string Text => Editor.Document.GetText();

    /// <summary>
    /// How many lines the buffer has.
    /// </summary>
    public int LineCount => Editor.Document.LineCount;

    /// <summary>
    /// The caret's line, counted from one.
    /// </summary>
    public int CaretLine => Editor.Document.OffsetToPosition(ClampedCaret).Line;

    /// <summary>
    /// The caret's column, counted from zero.
    /// </summary>
    public int CaretColumn => Editor.Document.OffsetToPosition(ClampedCaret).Column - 1;

    /// <summary>
    /// The caret's line.
    /// </summary>
    public string CurrentLine => Editor.Document.GetLineText(CaretLine);

    /// <summary>
    /// The caret's line up to the caret.
    /// </summary>
    public string TextBeforeCaret
    {
        get
        {
            var line = CurrentLine;
            return line[..Math.Min(CaretColumn, line.Length)];
        }
    }

    /// <summary>
    /// Whether a <c>/*</c> is open when a line of the buffer begins, counting from the engine's state.
    /// </summary>
    /// <param name="line">The line, counted from one.</param>
    /// <returns>True when a block comment is open there.</returns>
    public bool CommentOpenBefore(int line)
    {
        var open = Highlighter.CommentOpenAtStart;
        for (var i = 1; i < line && i <= LineCount; i++)
        {
            CilLexer.StripComments(Editor.Document.GetLineText(i), ref open);
        }

        return open;
    }

    /// <summary>
    /// Posts an event and asks for a frame. Safe from any thread.
    /// </summary>
    /// <param name="e">The event.</param>
    public void Post(SubmissionEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Events.Enqueue(e);
        Invalidate?.Invoke();
    }

    /// <summary>
    /// Replaces the buffer, puts the caret somewhere in it, and forgets the undo history.
    /// </summary>
    /// <param name="text">The new buffer.</param>
    /// <param name="caret">The caret's offset.</param>
    public void SetText(string text, int caret)
    {
        ArgumentNullException.ThrowIfNull(text);
        _pendingHistoryBacks = 0;
        var document = Editor.Document;
        Editor.Cursor.ClearSelection();
        ReturnedSelection = null;
        Anchors.Clear();
        document.Apply(new ReplaceOperation(new DocumentRange(DocumentOffset.Zero, new DocumentOffset(document.Length)), text));
        PendingDisplay = null;
        Editor.History.Clear();
        Editor.SetCursorPosition(new DocumentOffset(Math.Clamp(caret, 0, document.Length)));
        LastLength = document.Length;
    }

    /// <summary>
    /// Selects one line of the buffer and puts the caret at its end.
    /// </summary>
    /// <param name="line">The line, counted from zero.</param>
    public void SelectLine(int line)
    {
        var document = Editor.Document;
        var number = Math.Clamp(line + 1, 1, document.LineCount);
        var start = document.PositionToOffset(new DocumentPosition(number, 1));
        var end = new DocumentOffset(start.Value + document.GetLineText(number).Length);
        Editor.SetCursorPosition(start);
        Editor.SetCursorPosition(end, extend: true);
        ReturnedSelection = Editor.Cursor.SelectionRange;
        ReturnedVersion = document.Version;
    }

    /// <summary>
    /// How many rows the editor took on the last frame.
    /// </summary>
    public int LastEditorRows { get; set; } = 1;

    /// <summary>
    /// True when the editor's row count changed on the frame being built, so one more frame is
    /// drawn to settle the transcript.
    /// </summary>
    public bool RowsChanged { get; set; }

    /// <summary>
    /// The line a refusal selected, so Ctrl+C on it clears rather than copies; null once the
    /// buffer changes.
    /// </summary>
    public DocumentRange? ReturnedSelection { get; private set; }

    /// <summary>
    /// The document version the returned selection was made at; an edit since means the
    /// selection, whatever its offsets, is the user's own.
    /// </summary>
    public long ReturnedVersion { get; private set; }

    /// <summary>
    /// The document version when the transcript's copy mode was last seen to begin, so typing,
    /// which changes it, ends the selection; null while copy mode is off.
    /// </summary>
    public long? CopyModeVersion { get; set; }

    /// <summary>
    /// True while the current selection is the one a refusal made, untouched since.
    /// </summary>
    public bool SelectionIsReturned =>ReturnedSelection is { } returned && Editor.Document.Version == ReturnedVersion && Editor.Cursor.HasSelection && Editor.Cursor.SelectionRange == returned;

    /// <summary>
    /// The depth and comment state the next submission will start from: the engine's own while
    /// nothing is in flight, otherwise where the submission in flight and everything queued behind
    /// it will leave the engine, so a line typed meanwhile is judged against that and not against
    /// a block the worker is still closing.
    /// </summary>
    /// <param name="status">The engine's status now.</param>
    /// <returns>The open depth and whether a block comment is open.</returns>
    public (int Depth, bool CommentOpen) Expected(SessionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (Submission is not { } sending)
        {
            return (status.OpenDepth, status.Mark.InBlockComment);
        }

        var depth = sending.DepthAfter;
        var comment = sending.CommentOpenAfter;
        foreach (var text in Pending)
        {
            var scan = BlockBalance.Scan(text, depth, comment, commands: Commands);
            depth = Math.Max(0, scan.Depth);
            comment = scan.InBlockComment;
        }

        return (depth, comment);
    }

    /// <summary>
    /// Empties the buffer.
    /// </summary>
    public void Clear() => SetText("", 0);

    private DocumentOffset ClampedCaret => new(Math.Clamp(Editor.Cursor.Position.Value, 0, Editor.Document.Length));
}
