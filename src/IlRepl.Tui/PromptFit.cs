namespace IlRepl.Tui;

/// <summary>
/// How the rows of the terminal are shared between the transcript, the completion palette, and the editor.
/// </summary>
/// <param name="EditorRows">Rows for the editor, at least one.</param>
/// <param name="PaletteRows">Rows for the palette's candidates, its border not counted; zero hides it.</param>
/// <param name="TranscriptRows">Rows left for the transcript.</param>
public readonly record struct PromptFit(int EditorRows, int PaletteRows, int TranscriptRows);
