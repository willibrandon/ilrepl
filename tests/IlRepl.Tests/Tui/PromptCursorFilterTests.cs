using Hex1b.Tokens;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// The caret filter rewrites the text box's bar caret and nothing else.
/// </summary>
[TestClass]
public sealed class PromptCursorFilterTests
{
    /// <summary>
    /// Both bar shapes become a blinking block; block and underline shapes and other tokens are untouched.
    /// </summary>
    [TestMethod]
    public void Rewrite_TurnsBarsIntoBlinkingBlock()
    {
        Assert.AreEqual(CursorShapeToken.BlinkingBlock, PromptCursorFilter.Rewrite(CursorShapeToken.SteadyBar));
        Assert.AreEqual(CursorShapeToken.BlinkingBlock, PromptCursorFilter.Rewrite(CursorShapeToken.BlinkingBar));
        Assert.AreEqual(CursorShapeToken.BlinkingBlock, PromptCursorFilter.Rewrite(new UnrecognizedSequenceToken("\x1b[6 q")));
        Assert.AreEqual(CursorShapeToken.BlinkingBlock, PromptCursorFilter.Rewrite(new UnrecognizedSequenceToken("\x1b[5 q")));
        var other = new UnrecognizedSequenceToken("\x1b[2 q");
        Assert.AreSame(other, PromptCursorFilter.Rewrite(other));
        Assert.AreEqual(CursorShapeToken.SteadyBlock, PromptCursorFilter.Rewrite(CursorShapeToken.SteadyBlock));
        Assert.AreEqual(CursorShapeToken.SteadyUnderline, PromptCursorFilter.Rewrite(CursorShapeToken.SteadyUnderline));
        var text = new TextToken("il[1]>");
        Assert.AreSame(text, PromptCursorFilter.Rewrite(text));
    }

    /// <summary>
    /// The filter forwards every token, in order, with only the caret shape changed.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [TestMethod]
    public async Task OnOutput_ForwardsTokensInOrder()
    {
        var filter = new PromptCursorFilter();
        var applied = new[]
        {
            AppliedToken.WithNoCellImpacts(new TextToken("a"), 0, 0, 1, 0),
            AppliedToken.WithNoCellImpacts(new UnrecognizedSequenceToken("\x1b[6 q"), 1, 0, 1, 0),
            AppliedToken.WithNoCellImpacts(new TextToken("b"), 1, 0, 2, 0),
        };

        var forwarded = await filter.OnOutputAsync(applied, TimeSpan.Zero, TestContext.CancellationToken);

        Assert.AreEqual("a\x1b[1 qb", AnsiTokenSerializer.Serialize(forwarded));
    }

    /// <summary>
    /// The test context, for cancellation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;
}
