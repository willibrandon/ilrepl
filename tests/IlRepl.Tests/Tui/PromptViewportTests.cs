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
}
