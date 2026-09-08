using Hex1b;
using Hex1b.Layout;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Adds the prompt to a widget context.
/// </summary>
public static class PromptWidgetExtensions
{
    /// <summary>
    /// Creates the prompt.
    /// </summary>
    /// <typeparam name="TParent">The parent widget type.</typeparam>
    /// <param name="context">The widget context.</param>
    /// <param name="label">The prompt shown on the first line.</param>
    /// <param name="catalog">Every opcode and command the palette can offer.</param>
    /// <param name="state">The prompt's state.</param>
    /// <param name="fit">How many rows the editor and the palette get.</param>
    /// <param name="openDepth">How many closing braces the engine is already waiting for.</param>
    /// <param name="commentOpen">Whether the engine has a <c>/*</c> open when the buffer starts.</param>
    /// <returns>The prompt.</returns>
    public static PromptWidget IlPrompt<TParent>(this WidgetContext<TParent> context, string label, IReadOnlyList<CompletionItem> catalog, PromptState state, PromptFit fit, int openDepth, bool commentOpen)
        where TParent : Hex1bWidget
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(state);
        // The prompt is as tall as its editor and, when it shows, its palette with the border,
        // so the stack above gives it exactly those rows and the transcript takes the rest.
        var palette = fit.PaletteRows > 0 && PromptWidget.Candidates(state, catalog).Count > 0 ? fit.PaletteRows + 2 : 0;
        return new PromptWidget(label, catalog, state, fit, openDepth, commentOpen) { HeightHint = SizeHint.Fixed(Math.Max(1, fit.EditorRows) + palette) };
    }
}
