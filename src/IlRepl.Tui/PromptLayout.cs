namespace IlRepl.Tui;

/// <summary>
/// Shares the terminal's rows: the status bar and the separator come first, then the editor
/// grows with its lines up to a third of the terminal, then the palette with its border, and the
/// transcript takes the rest. On a short terminal the palette goes before the editor shrinks.
/// </summary>
public static class PromptLayout
{
    /// <summary>
    /// The most candidate rows the palette shows.
    /// </summary>
    public const int MaxPaletteRows = 8;

    /// <summary>
    /// The fewest candidate rows worth showing.
    /// </summary>
    public const int MinPaletteRows = 3;

    private const int FixedRows = 2;
    private const int PaletteBorderRows = 2;
    private const int DefaultHeight = 24;

    /// <summary>
    /// Fits the editor and the palette to the terminal.
    /// </summary>
    /// <param name="terminalHeight">The terminal's height, or zero when it is not known yet.</param>
    /// <param name="lineCount">How many lines the buffer has.</param>
    /// <param name="candidateCount">How many completion candidates there are to show.</param>
    /// <param name="detailLines">The wrapped lines needed by the selected candidate's complete signature.</param>
    /// <returns>The rows each part gets.</returns>
    public static PromptFit Fit(int terminalHeight, int lineCount, int candidateCount, int detailLines = 0)
    {
        var height = terminalHeight <= 0 ? DefaultHeight : terminalHeight;
        var editorRows = height < 8 ? 1 : Math.Clamp(lineCount, 1, Math.Max(1, height / 3));
        var remaining = Math.Max(0, height - FixedRows - editorRows);
        var paletteRows = 0;
        if (candidateCount > 0 && height >= 8)
        {
            var available = remaining - PaletteBorderRows - 1;
            var wanted = Math.Min(candidateCount, MaxPaletteRows);
            paletteRows = available >= MinPaletteRows ? Math.Min(wanted, available) : 0;
        }

        var detailRows = 0;
        if (paletteRows > 0 && detailLines > 0)
        {
            var available = remaining - PaletteBorderRows - Math.Min(MinPaletteRows, paletteRows) - 1;
            detailRows = available >= 2 ? Math.Min(detailLines + 1, available) : 0;
            paletteRows = Math.Min(paletteRows, remaining - PaletteBorderRows - detailRows - 1);
        }

        var transcriptRows = remaining - (paletteRows > 0 ? paletteRows + PaletteBorderRows + detailRows : 0);
        return new PromptFit(editorRows, paletteRows, Math.Max(0, transcriptRows)) { DetailRows = detailRows };
    }
}
