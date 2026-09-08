using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// One event on the prompt's queue.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Lines">Transcript lines to add, for <see cref="SubmissionEventKind.Lines"/>.</param>
/// <param name="Text">Text for the editor: a paste's payload, or the lines coming back after a refusal, a failure, or a cancel.</param>
/// <param name="CaretLine">Which line of <paramref name="Text"/> the caret goes to, counted from zero.</param>
/// <param name="Note">An information line to add to the transcript, or null.</param>
/// <param name="Entries">History entries, for <see cref="SubmissionEventKind.HistoryLoaded"/>.</param>
/// <param name="Own">How many of the entries, at the end, this session wrote itself before they were read.</param>
public sealed record SubmissionEvent(
    SubmissionEventKind Kind,
    IReadOnlyList<TranscriptLine>? Lines = null,
    string? Text = null,
    int CaretLine = 0,
    string? Note = null,
    IReadOnlyList<string>? Entries = null,
    int Own = 0)
{
    /// <summary>
    /// A paste with its payload.
    /// </summary>
    /// <param name="text">The payload.</param>
    /// <returns>The event.</returns>
    public static SubmissionEvent Paste(string text) => new(SubmissionEventKind.Paste, Text: text);

    /// <summary>
    /// Lines the engine answered with.
    /// </summary>
    /// <param name="lines">The lines.</param>
    /// <returns>The event.</returns>
    public static SubmissionEvent Reply(IReadOnlyList<TranscriptLine> lines) => new(SubmissionEventKind.Lines, Lines: lines);

    /// <summary>
    /// A refused line: the text comes back with the refused line selected.
    /// </summary>
    /// <param name="lines">The last reply's lines and the withdrawal's.</param>
    /// <param name="text">The text coming back.</param>
    /// <param name="caretLine">The refused line within it.</param>
    /// <param name="note">A note to add, or null.</param>
    /// <returns>The event.</returns>
    public static SubmissionEvent Refused(IReadOnlyList<TranscriptLine> lines, string text, int caretLine, string? note = null) => new(SubmissionEventKind.Refused, Lines: lines, Text: text, CaretLine: caretLine, Note: note);

    /// <summary>
    /// A failure: the unsent text comes back and the message goes to the transcript.
    /// </summary>
    /// <param name="lines">The last reply's lines.</param>
    /// <param name="message">What went wrong, or null when the transcript already says.</param>
    /// <param name="text">The text coming back.</param>
    /// <returns>The event.</returns>
    public static SubmissionEvent Failure(IReadOnlyList<TranscriptLine> lines, string? message, string text) => new(SubmissionEventKind.Failed, Lines: lines, Text: text, Note: message);

    /// <summary>
    /// A cancel: the text still to send comes back.
    /// </summary>
    /// <param name="lines">The lines the withdrawal produced.</param>
    /// <param name="text">The text coming back.</param>
    /// <returns>The event.</returns>
    public static SubmissionEvent Cancel(IReadOnlyList<TranscriptLine> lines, string text) => new(SubmissionEventKind.Cancelled, Lines: lines, Text: text);

    /// <summary>
    /// The user asked to leave.
    /// </summary>
    /// <param name="lines">The last reply's lines.</param>
    /// <returns>The event.</returns>
    public static SubmissionEvent QuitRequested(IReadOnlyList<TranscriptLine> lines) => new(SubmissionEventKind.Quit, Lines: lines);

    /// <summary>
    /// Every line was sent.
    /// </summary>
    /// <param name="lines">The last reply's lines.</param>
    /// <returns>The event.</returns>
    public static SubmissionEvent Done(IReadOnlyList<TranscriptLine> lines) => new(SubmissionEventKind.Completed, Lines: lines);

    /// <summary>
    /// The history store finished loading.
    /// </summary>
    /// <param name="entries">The entries, oldest first.</param>
    /// <param name="own">How many of the entries, at the end, this session wrote itself before they were read.</param>
    /// <returns>The event.</returns>
    public static SubmissionEvent HistoryLoaded(IReadOnlyList<string> entries, int own = 0) => new(SubmissionEventKind.HistoryLoaded, Entries: entries, Own: own);
}
