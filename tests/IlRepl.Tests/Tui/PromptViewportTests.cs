using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests for <see cref="PromptViewport"/>: the caret is always inside the view, and the view
/// moves only when it has to.
/// </summary>
[TestClass]
public sealed class PromptViewportTests
{
    /// <summary>
    /// A caret already in view moves nothing.
    /// </summary>
    [TestMethod]
    public void Reveal_CaretVisible_NoMovement()
    {
        var offsets = new ViewportOffsets(3, 4);
        Assert.AreEqual(offsets, PromptViewport.Reveal(offsets, 5, 10, caretLine: 3, caretColumn: 4, lineCount: 20));
        Assert.AreEqual(offsets, PromptViewport.Reveal(offsets, 5, 10, caretLine: 7, caretColumn: 13, lineCount: 20));
    }

    /// <summary>
    /// A caret below the view brings the view down so the caret sits on the last row; above, so it sits on the first.
    /// </summary>
    [TestMethod]
    public void Reveal_CaretOutsideVertically_MovesTheLeastItCan()
    {
        Assert.AreEqual(new ViewportOffsets(6, 0), PromptViewport.Reveal(new ViewportOffsets(1, 0), 5, 10, caretLine: 10, caretColumn: 0, lineCount: 20));
        Assert.AreEqual(new ViewportOffsets(2, 0), PromptViewport.Reveal(new ViewportOffsets(6, 0), 5, 10, caretLine: 2, caretColumn: 0, lineCount: 20));
        Assert.AreEqual(new ViewportOffsets(1, 0), PromptViewport.Reveal(new ViewportOffsets(1, 0), 5, 10, caretLine: 5, caretColumn: 0, lineCount: 20));
    }

    /// <summary>
    /// A caret past the right edge brings the view right so the caret cell is the last column; past the left, so it is the first.
    /// </summary>
    [TestMethod]
    public void Reveal_CaretOutsideHorizontally_MovesTheLeastItCan()
    {
        Assert.AreEqual(new ViewportOffsets(1, 3), PromptViewport.Reveal(new ViewportOffsets(1, 0), 5, 10, caretLine: 1, caretColumn: 12, lineCount: 1));
        Assert.AreEqual(new ViewportOffsets(1, 0), PromptViewport.Reveal(new ViewportOffsets(1, 0), 5, 10, caretLine: 1, caretColumn: 9, lineCount: 1));
        Assert.AreEqual(new ViewportOffsets(1, 2), PromptViewport.Reveal(new ViewportOffsets(1, 5), 5, 10, caretLine: 1, caretColumn: 2, lineCount: 1));
    }

    /// <summary>
    /// When the document shrinks under the view, the view is clamped to what is left before the caret is checked.
    /// </summary>
    [TestMethod]
    public void Reveal_DocumentShrank_ClampsFirst()
    {
        Assert.AreEqual(new ViewportOffsets(1, 0), PromptViewport.Reveal(new ViewportOffsets(10, 0), 5, 10, caretLine: 3, caretColumn: 0, lineCount: 3));
        Assert.AreEqual(new ViewportOffsets(16, 0), PromptViewport.Reveal(new ViewportOffsets(30, 0), 5, 10, caretLine: 20, caretColumn: 0, lineCount: 20));
    }

    /// <summary>
    /// A viewport with no rows or columns is treated as one by one, and offsets never go below their floor.
    /// </summary>
    [TestMethod]
    public void Reveal_DegenerateViewport_StaysSane()
    {
        Assert.AreEqual(new ViewportOffsets(4, 7), PromptViewport.Reveal(new ViewportOffsets(0, -1), 0, 0, caretLine: 4, caretColumn: 7, lineCount: 4));
    }

    /// <summary>
    /// The offsets count characters and the screen counts cells, so a line of wide characters
    /// scrolls further than its character count says; narrow text is left as it was.
    /// </summary>
    [TestMethod]
    public void RevealWide_WideCharacters_ScrollUntilTheCaretCellFits()
    {
        var line = "ldstr \"" + new string('漢', 40) + "\"";
        var caret = line.Length;
        var offsets = PromptView.RevealWide(new ViewportOffsets(1, 0), 40, line, caret);
        Assert.IsGreaterThan(0, offsets.Left);
        Assert.IsLessThan(40, Hex1b.DisplayWidth.GetStringWidth(line[offsets.Left..caret]), "the cells before the caret fit");
        Assert.IsGreaterThanOrEqualTo(40, Hex1b.DisplayWidth.GetStringWidth(line[(offsets.Left - 1)..caret]), "and no further than needed");
        Assert.AreEqual(1, offsets.Top);

        Assert.AreEqual(new ViewportOffsets(1, 0), PromptView.RevealWide(new ViewportOffsets(1, 0), 40, "ldstr \"narrow\"", 14));
        Assert.AreEqual(new ViewportOffsets(1, 3), PromptView.RevealWide(new ViewportOffsets(1, 3), 40, line, 5), "a caret before the offset is left to the character reveal");
    }

    /// <summary>
    /// Scrolling never starts inside a surrogate pair or a joined emoji: the offset lands on a
    /// text element, and one whose character index fell inside a pair is moved to its start.
    /// </summary>
    [TestMethod]
    public void RevealWide_Emoji_KeepsTextElementBoundaries()
    {
        var line = "ldstr \"" + string.Concat(Enumerable.Repeat("😀", 40)) + "\"";
        var caret = line.Length;
        var offsets = PromptView.RevealWide(new ViewportOffsets(1, 0), 30, line, caret);
        Assert.IsGreaterThan(0, offsets.Left);
        Assert.IsFalse(char.IsLowSurrogate(line[offsets.Left]), "the scroll starts on a whole character");
        Assert.IsLessThan(30, Hex1b.DisplayWidth.GetStringWidth(line[offsets.Left..caret]));

        var inside = PromptView.RevealWide(new ViewportOffsets(1, 8), 200, line, caret);
        Assert.AreEqual(7, inside.Left, "an offset inside a pair moves to the pair's start");

        var joined = "ldstr \"" + string.Concat(Enumerable.Repeat("👩\u200D💻", 20)) + "\"";
        var family = PromptView.RevealWide(new ViewportOffsets(1, 0), 20, joined, joined.Length);
        var elements = new List<int>();
        for (var i = 0; i < joined.Length; i += System.Globalization.StringInfo.GetNextTextElementLength(joined.AsSpan(i)))
        {
            elements.Add(i);
        }

        Assert.Contains(family.Left, elements, "the scroll starts on a whole joined emoji");
    }

    /// <summary>
    /// The horizontal reveal counts cells, not characters: a line of decomposed accents, many
    /// characters but few cells, that fits the columns is not scrolled at all; a line of wide
    /// characters is scrolled by whole characters until the caret's cell fits; and a joined emoji
    /// is never split.
    /// </summary>
    [TestMethod]
    public void RevealCaret_CountsCellsAndLandsOnTextElements()
    {
        var accents = "ldstr \"" + string.Concat(Enumerable.Repeat("e\u0301", 60)) + "\"";
        Assert.AreEqual(128, accents.Length);
        Assert.AreEqual(68, Hex1b.DisplayWidth.GetStringWidth(accents));
        var fits = PromptView.RevealCaret(new ViewportOffsets(1, 0), 1, 73, accents, 1, accents.Length, 1);
        Assert.AreEqual(new ViewportOffsets(1, 0), fits, "a line that fits is not scrolled");

        var wide = "ldstr \"" + new string('漢', 45) + "\"";
        var scrolled = PromptView.RevealCaret(new ViewportOffsets(1, 0), 1, 73, wide, 1, wide.Length, 1);
        Assert.IsGreaterThan(0, scrolled.Left);
        Assert.IsLessThan(73, Hex1b.DisplayWidth.GetStringWidth(wide[scrolled.Left..]), "the caret's cell fits");
        Assert.IsGreaterThanOrEqualTo(73, Hex1b.DisplayWidth.GetStringWidth(wide[(scrolled.Left - 1)..]), "and no further than needed");
        Assert.AreEqual(new ViewportOffsets(1, 0), PromptView.RevealCaret(scrolled, 1, 73, wide, 1, 0, 1), "Home scrolls back to the start");

        var joined = "ldstr \"" + string.Concat(Enumerable.Repeat("👩\u200D💻", 30)) + "\"";
        var family = PromptView.RevealCaret(new ViewportOffsets(1, 0), 1, 40, joined, 1, joined.Length, 1);
        var elements = new List<int>();
        for (var i = 0; i < joined.Length; i += System.Globalization.StringInfo.GetNextTextElementLength(joined.AsSpan(i)))
        {
            elements.Add(i);
        }

        Assert.Contains(family.Left, elements, "the scroll starts on a whole joined emoji");
        Assert.IsLessThan(40, Hex1b.DisplayWidth.GetStringWidth(joined[family.Left..]));
    }

    /// <summary>
    /// A style cut at the scrolled edge starts at the next character that is a whole code unit,
    /// so it never begins inside a character built from several: in a run of joined emoji that
    /// is the closing quote, and in plain text it is the edge itself.
    /// </summary>
    [TestMethod]
    public void SafeStart_SkipsSurrogatePairsAtTheEdge()
    {
        var joined = "ldstr \"" + string.Concat(Enumerable.Repeat("👩\u200D💻", 3)) + "\" nop";
        Assert.AreEqual(joined.IndexOf('"', 8), ScrolledDecorations.SafeStart(joined, 12), "the quote is the first whole code unit after the edge");
        Assert.AreEqual(joined.IndexOf('"', 8), ScrolledDecorations.SafeStart(joined, 14), "an edge inside a character moves on the same way");
        Assert.AreEqual(3, ScrolledDecorations.SafeStart("ldc.i4 1", 3), "plain text starts at the edge");
        Assert.AreEqual(9, ScrolledDecorations.SafeStart("ldstr \"漢漢\"", 9), "wide characters that are one code unit are fine");
        Assert.AreEqual(4, ScrolledDecorations.SafeStart("😀😀", 1), "no whole code unit follows");
    }
}
