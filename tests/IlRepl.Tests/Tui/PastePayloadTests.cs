using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests for <see cref="PastePayload"/>: only the clipboard's own terminator comes off.
/// </summary>
[TestClass]
public sealed class PastePayloadTests
{
    /// <summary>
    /// One final newline is the terminator; every newline beyond it is a blank line of the user's.
    /// </summary>
    [TestMethod]
    public void Prepare_RemovesExactlyOneTrailingNewline()
    {
        Assert.AreEqual("ret", PastePayload.Prepare("ret"));
        Assert.AreEqual("ret", PastePayload.Prepare("ret\n"));
        Assert.AreEqual("ret\n", PastePayload.Prepare("ret\n\n"));
        Assert.AreEqual("ret\n\n", PastePayload.Prepare("ret\n\n\n"));
        Assert.AreEqual("", PastePayload.Prepare("\n"));
        Assert.AreEqual("", PastePayload.Prepare(""));
    }

    /// <summary>
    /// The exact two-cell payload keeps the blank line between the cells and the one after the second.
    /// </summary>
    [TestMethod]
    public void Prepare_TwoCellPayload()
    {
        Assert.AreEqual("ldc.i4.1\n\nldc.i4.2\n", PastePayload.Prepare("ldc.i4.1\n\nldc.i4.2\n\n"));
        Assert.AreEqual("ldc.i4.1\n\nldc.i4.2", PastePayload.Prepare("ldc.i4.1\n\nldc.i4.2\n"));
    }

    /// <summary>
    /// Windows and old Mac line endings become newlines first.
    /// </summary>
    [TestMethod]
    public void Prepare_FoldsLineEndings()
    {
        Assert.AreEqual("a\nb", PastePayload.Prepare("a\r\nb\r\n"));
        Assert.AreEqual("a\nb\n", PastePayload.Prepare("a\rb\r\r"));
    }
}
