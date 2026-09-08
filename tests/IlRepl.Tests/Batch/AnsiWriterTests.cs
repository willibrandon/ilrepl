using IlRepl.Batch;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Batch;

/// <summary>
/// Tests for <see cref="AnsiWriter"/>: the batch front-end colours the same spans the terminal does.
/// </summary>
[TestClass]
public sealed class AnsiWriterTests
{
    /// <summary>
    /// A tokenized echo renders the opcode and the number in their codes, and plain text without.
    /// </summary>
    [TestMethod]
    public void Render_TokenizedLine_UsesCodes()
    {
        var core = new ReplCore();
        core.Handle("ldc.i4 6 // six");
        var echo = core.Transcript.Lines[0];
        var rendered = AnsiWriter.Render(echo, color: true);
        Assert.Contains("\x1b[36mldc.i4\x1b[0m", rendered);
        Assert.Contains("\x1b[33m6\x1b[0m", rendered);
        Assert.Contains("\x1b[2m// six\x1b[0m", rendered);
        Assert.AreEqual("il[1]> ldc.i4 6 // six", AnsiWriter.Render(echo, color: false));
    }

    /// <summary>
    /// Every style has a code or renders plain; none throws.
    /// </summary>
    [TestMethod]
    public void Render_EveryStyle_Renders()
    {
        foreach (var style in Enum.GetValues<SpanStyle>())
        {
            var line = new TranscriptLine(LineKind.Listing, [new TranscriptSpan("x", style)]);
            Assert.EndsWith("x" + (AnsiWriter.Render(line, color: true).Contains('\x1b', StringComparison.Ordinal) ? "\x1b[0m" : ""), AnsiWriter.Render(line, color: true), style.ToString());
        }
    }
}
