using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests for <see cref="PromptLayout"/>: how the rows are shared at every height.
/// </summary>
[TestClass]
public sealed class PromptLayoutTests
{
    /// <summary>
    /// The editor grows with its lines up to a third of the terminal and never past its lines.
    /// </summary>
    [TestMethod]
    public void Fit_EditorFollowsLinesUpToAThird()
    {
        Assert.AreEqual(new PromptFit(1, 0, 37), PromptLayout.Fit(40, 1, 0));
        Assert.AreEqual(new PromptFit(2, 0, 36), PromptLayout.Fit(40, 2, 0));
        Assert.AreEqual(new PromptFit(13, 0, 25), PromptLayout.Fit(40, 20, 0));
        Assert.AreEqual(new PromptFit(8, 0, 14), PromptLayout.Fit(24, 100, 0));
        Assert.AreEqual(1, PromptLayout.Fit(40, 0, 0).EditorRows);
    }

    /// <summary>
    /// The palette takes up to eight candidate rows plus its border, and never the last transcript row.
    /// </summary>
    [TestMethod]
    public void Fit_PaletteFitsAfterTheEditor()
    {
        Assert.AreEqual(new PromptFit(1, 8, 11), PromptLayout.Fit(24, 1, 10));
        Assert.AreEqual(new PromptFit(1, 5, 14), PromptLayout.Fit(24, 1, 5));
        Assert.AreEqual(new PromptFit(8, 8, 4), PromptLayout.Fit(24, 8, 10));
        Assert.AreEqual(new PromptFit(4, 3, 1), PromptLayout.Fit(12, 4, 5));
    }

    /// <summary>
    /// On a short terminal the palette goes first; under eight rows the editor shows one row.
    /// </summary>
    [TestMethod]
    public void Fit_ShortTerminal_DropsThePaletteThenTheEditor()
    {
        Assert.AreEqual(new PromptFit(2, 0, 4), PromptLayout.Fit(8, 3, 5));
        Assert.AreEqual(new PromptFit(1, 0, 3), PromptLayout.Fit(6, 3, 5));
        Assert.AreEqual(new PromptFit(1, 0, 0), PromptLayout.Fit(3, 3, 5));
        Assert.AreEqual(new PromptFit(1, 0, 0), PromptLayout.Fit(1, 1, 0));
    }

    /// <summary>
    /// An unknown height is treated as twenty-four rows.
    /// </summary>
    [TestMethod]
    public void Fit_UnknownHeight_IsTwentyFour()
    {
        Assert.AreEqual(PromptLayout.Fit(24, 3, 4), PromptLayout.Fit(0, 3, 4));
    }
}
