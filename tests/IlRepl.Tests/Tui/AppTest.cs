using Hex1b;
using Hex1b.Automation;
using IlRepl.Protocol;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// What every full-stack prompt test starts from: the real app on the headless terminal.
/// </summary>
internal static class AppTest
{
    /// <summary>
    /// The default wait for anything the screen should show.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Builds the app on a headless terminal.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="transcript">The transcript.</param>
    /// <param name="width">The width in columns.</param>
    /// <param name="height">The height in rows.</param>
    /// <param name="configure">Extra builder steps.</param>
    /// <param name="history">The history store, or none.</param>
    /// <param name="onPrompt">Receives the prompt's state.</param>
    /// <returns>The terminal, not yet running.</returns>
    public static Hex1bTerminal Build(IReplEngine engine, Transcript transcript, int width = 100, int height = 30, Func<Hex1bTerminalBuilder, Hex1bTerminalBuilder>? configure = null, IHistoryStore? history = null, Action<PromptState>? onPrompt = null)
    {
        var builder = IlReplApp.Configure(Hex1bTerminal.CreateBuilder(), engine, transcript, history: history, onPrompt: onPrompt)
            .WithHeadless()
            .WithDimensions(width, height);
        return (configure?.Invoke(builder) ?? builder).Build();
    }

    /// <summary>
    /// The caret cell in a snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The cell, or null.</returns>
    public static (int X, int Y)? Caret(Hex1bTerminalSnapshot snapshot) => FrameRecorder.FindCaret(snapshot);

    /// <summary>
    /// A row's text without trailing blanks.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="y">The row.</param>
    /// <returns>The text.</returns>
    public static string Row(Hex1bTerminalSnapshot snapshot, int y) => snapshot.GetLine(y).TrimEnd();

    /// <summary>
    /// The screen row of the prompt's first visible line: the prompt's rows sit just above the status bar.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The row, or -1 while no prompt is on screen, so a wait can keep polling.</returns>
    public static int PromptTop(Hex1bTerminalSnapshot snapshot)
    {
        var top = snapshot.Height - 1;
        for (var y = snapshot.Height - 2; y >= 0; y--)
        {
            var row = snapshot.GetLine(y);
            if (row.StartsWith("il[", StringComparison.Ordinal) || row.StartsWith("  ...> ", StringComparison.Ordinal))
            {
                top = y;
            }
            else
            {
                break;
            }
        }

        return top == snapshot.Height - 1 ? -1 : top;
    }

    /// <summary>
    /// A prompt row's text without trailing blanks, counted from the prompt's first visible line.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="line">The visible line, zero based.</param>
    /// <returns>The text.</returns>
    public static string PromptRow(Hex1bTerminalSnapshot snapshot, int line)
    {
        var top = PromptTop(snapshot);
        return top < 0 || top + line >= snapshot.Height ? "" : Row(snapshot, top + line);
    }

    /// <summary>
    /// The visible prompt line the caret is on, or -1.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The line, zero based from the prompt's first visible line.</returns>
    public static int CaretLine(Hex1bTerminalSnapshot snapshot) => Caret(snapshot) is { } c && PromptTop(snapshot) is var top && top >= 0 ? c.Y - top : -1;

    /// <summary>
    /// True when the caret cell is at a column of a visible prompt line.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="x">The column.</param>
    /// <param name="line">The visible line, zero based from the prompt's first visible line.</param>
    /// <returns>True when the caret is there.</returns>
    public static bool CaretAt(Hex1bTerminalSnapshot snapshot, int x, int line) => PromptTop(snapshot) is var top && top >= 0 && Caret(snapshot) == (x, top + line);

    /// <summary>
    /// The echoed input lines, in order.
    /// </summary>
    /// <param name="transcript">The transcript.</param>
    /// <returns>The plain text of every input line.</returns>
    public static List<string> Echoes(Transcript transcript) => transcript.Lines.Where(l => l.Kind == LineKind.Input).Select(l => l.PlainText).ToList();

    /// <summary>
    /// Types lines, each followed by Enter.
    /// </summary>
    /// <param name="auto">The automator.</param>
    /// <param name="lines">The lines.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes once the keys are queued.</returns>
    public static async Task TypeLinesAsync(Hex1bTerminalAutomator auto, IEnumerable<string> lines, CancellationToken ct)
    {
        foreach (var line in lines)
        {
            await auto.TypeAsync(line, ct: ct);
            await auto.EnterAsync(ct: ct);
        }
    }
}
