using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Quoted comparison arguments retain punctuation and escapes through real worker execution.
/// </summary>
[TestClass]
public sealed class ComparisonLiteralTests
{
    /// <summary>
    /// Supplies cancellation for isolated comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Commas, parentheses, quotes, and trailing backslashes remain inside their literal argument.
    /// </summary>
    /// <param name="value">The literal string to echo.</param>
    [TestMethod]
    [DataRow("a,b")]
    [DataRow(") --assert (")]
    [DataRow("a\"),\"b")]
    [DataRow("quote ' and slash \\")]
    [DataRow("[<,>](\n)")]
    public async Task Compare_QuotedPunctuationIsPassedIntact(string value)
    {
        var session = IlLines.Load(".method string Echo(string value) {", "ldarg.0", "ret", "}");
        var edit = session.PrepareEdit("Echo", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var package = ComparisonCapture.Create(session, "Copy (" + LiteralParser.Escape(value) + ") --assert");
        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual(value, result.Original.Result!.Value);
        Assert.AreEqual(value, result.Edited.Result!.Value);
    }

    /// <summary>
    /// Unterminated or missing literals fail before constructing a runnable comparison package.
    /// </summary>
    /// <param name="arguments">The malformed argument list.</param>
    [TestMethod]
    [DataRow("(\"unterminated)")]
    [DataRow("('unterminated)")]
    [DataRow("(\"escaped\\\")")]
    [DataRow("(1,")]
    [DataRow("(1,)")]
    [DataRow("(,1)")]
    [DataRow("(float64(1)")]
    public void Parse_MalformedArgumentsAreRejected(string arguments)
    {
        Assert.ThrowsExactly<ReplException>(() => ComparisonCommand.Parse("Copy " + arguments));
    }
}
