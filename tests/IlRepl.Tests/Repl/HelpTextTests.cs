using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Checks help examples against execution and the colours of the real input echo.
/// </summary>
[TestClass]
public sealed class HelpTextTests
{
    /// <summary>
    /// Every complete example runs independently and uses the same syntax colours as the REPL.
    /// </summary>
    [TestMethod]
    [DataRow("Multiply two numbers", "  = 42 : int32")]
    [DataRow("Print a string", "  = (void)")]
    [DataRow("Count to ten", "  = 10 : int32")]
    [DataRow("Catch an exception", "  = \"boom\" : string")]
    public void Example_RunsWithEchoColours(string title, string expected)
    {
        var core = new ReplCore();
        var example = HelpText.Lines().SkipWhile(line => line.PlainText != "  " + title).Skip(1)
            .TakeWhile(line => line.PlainText.Length > 0).ToArray();
        Assert.IsNotEmpty(example);
        foreach (var line in example)
        {
            var before = core.Transcript.Lines.Count;
            var input = line.PlainText[2..];
            Assert.IsTrue(core.Handle(input).Succeeded, input);
            var echo = core.Transcript.Lines[before];
            var expectedStyles = echo.Spans.Skip(1).SelectMany(span => Enumerable.Repeat(span.Style, span.Text.Length));
            var helpStyles = line.Spans.SelectMany(span => Enumerable.Repeat(span.Style, span.Text.Length)).Skip(2);
            Assert.AreSequenceEqual(expectedStyles.ToArray(), helpStyles.ToArray(), input);
        }

        Assert.AreEqual(expected, core.Transcript.Lines[^1].PlainText);
        if (title == "Print a string")
        {
            Assert.Contains(line => line.Kind == LineKind.Output && line.PlainText == "hi", core.Transcript.Lines);
        }
    }

    /// <summary>
    /// Every command has a separate indented description regardless of its syntax length.
    /// </summary>
    [TestMethod]
    public void Entries_KeepDescriptionsOnSeparateLines()
    {
        var lines = HelpText.Lines();
        var entries = lines.Select((line, index) => (line, index))
            .Where(item => item.line.Spans.Count == 1 && item.line.Spans[0].Style == SpanStyle.Command).ToArray();
        Assert.IsNotEmpty(entries);
        foreach (var (line, index) in entries)
        {
            Assert.StartsWith("  ", line.PlainText);
            Assert.StartsWith("    ", lines[index + 1].PlainText);
            Assert.AreEqual(SpanStyle.Default, lines[index + 1].Spans[0].Style);
        }

        foreach (var command in Completer.Commands)
        {
            Assert.Contains(item => item.line.PlainText.TrimStart().Split(' ')[0] == command.Name, entries, command.Name);
        }
    }
}
