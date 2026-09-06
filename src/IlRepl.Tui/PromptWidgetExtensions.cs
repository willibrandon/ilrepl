using Hex1b;
using Hex1b.Widgets;
using IlRepl.Protocol;

namespace IlRepl.Tui;

/// <summary>
/// Fluent construction of <see cref="PromptWidget"/> from a widget context.
/// </summary>
public static class PromptWidgetExtensions
{
    /// <summary>
    /// Creates the REPL prompt.
    /// </summary>
    /// <typeparam name="TParent">The parent widget type.</typeparam>
    /// <param name="context">The widget context.</param>
    /// <param name="label">The prompt label.</param>
    /// <param name="catalog">Every opcode and command the palette can offer.</param>
    /// <returns>The prompt widget.</returns>
    public static PromptWidget IlPrompt<TParent>(this WidgetContext<TParent> context, string label, IReadOnlyList<CompletionItem> catalog)
        where TParent : Hex1bWidget
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(catalog);
        return new PromptWidget(label, catalog);
    }
}
