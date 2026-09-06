using Hex1b;
using Hex1b.Widgets;

namespace IlRepl.Tui;

/// <summary>
/// What the user sees after a yank: the copied rows flash for a moment and the status bar says
/// what was copied for a moment longer. Both clear on their own.
/// </summary>
public sealed class YankFeedback
{
    private long _generation;

    /// <summary>
    /// How long the copied rows stay highlighted.
    /// </summary>
    public static TimeSpan FlashDuration { get; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long the status bar keeps the message.
    /// </summary>
    public static TimeSpan NotificationDuration { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The message for the status bar, or null when there is none.
    /// </summary>
    public string? Notification { get; private set; }

    /// <summary>
    /// The first row of the flashed range in transcript rows, or -1 when nothing is flashing.
    /// </summary>
    public int FlashTop { get; private set; } = -1;

    /// <summary>
    /// One past the last row of the flashed range in transcript rows.
    /// </summary>
    public int FlashBottom { get; private set; } = -1;

    /// <summary>
    /// True when a line occupying the given rows should flash.
    /// </summary>
    /// <param name="firstRow">The line's first row in the transcript.</param>
    /// <param name="rowCount">How many rows the line takes.</param>
    /// <returns>True to flash the line.</returns>
    public bool Covers(int firstRow, int rowCount) => FlashTop >= 0 && firstRow < FlashBottom && firstRow + rowCount > FlashTop;

    /// <summary>
    /// Starts the flash and the message for a copy, and schedules both to clear.
    /// </summary>
    /// <param name="app">The app to redraw.</param>
    /// <param name="args">The copy that was made.</param>
    public void Show(Hex1bApp app, SelectionPanelCopyEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);
        var generation = Interlocked.Increment(ref _generation);
        var text = args.Text;
        var lines = text.Count(c => c == '\n') + 1;
        Notification = lines > 1 ? $"Yanked {lines} lines" : "Yanked: " + Shorten(text.Trim(), 40);
        FlashTop = args.PanelBounds.Y;
        FlashBottom = args.PanelBounds.Y + args.PanelBounds.Height;
        app.Invalidate();

        _ = ClearLaterAsync(app, generation);
    }

    private async Task ClearLaterAsync(Hex1bApp app, long generation)
    {
        await Task.Delay(FlashDuration).ConfigureAwait(false);
        if (Interlocked.Read(ref _generation) == generation)
        {
            FlashTop = -1;
            FlashBottom = -1;
            app.Invalidate();
        }

        await Task.Delay(NotificationDuration - FlashDuration).ConfigureAwait(false);
        if (Interlocked.Read(ref _generation) == generation)
        {
            Notification = null;
            app.Invalidate();
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
