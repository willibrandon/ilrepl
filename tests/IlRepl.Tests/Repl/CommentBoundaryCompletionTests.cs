using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Completion resumes at the exclusive end of a closed block comment without entering unfinished comments.
/// </summary>
[TestClass]
public sealed class CommentBoundaryCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A type inserted immediately after a comment preserves the comment and executes without extra whitespace.
    /// </summary>
    /// <param name="line">The instruction before the caret.</param>
    [TestMethod]
    [DataRow("box /* comment */")]
    [DataRow("box /**/")]
    [DataRow("box /* first *//**/")]
    public async Task Complete_ClosedComment_BindsAndRuns(string line)
    {
        var session = new Session();
        session.AddLine("ldc.i4.7");
        using var completer = new OperandCompleter(session);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, line.Length, null, [], true),
            TestContext.CancellationToken);
        Assert.AreEqual(line.Length, reply.ReplaceStart);
        Assert.AreEqual(0, reply.ReplaceLength);
        var chosen = reply.Items.Single(item => item.InsertText == "int32");
        session.AddLine(line + chosen.InsertText);
        Assert.AreEqual(7, session.Run().Value);
    }

    /// <summary>
    /// Line comments, unfinished blocks, and positions inside a closing delimiter still suppress completion.
    /// </summary>
    /// <param name="text">The instruction with a marked caret.</param>
    [TestMethod]
    [DataRow("box /* comment *|/")]
    [DataRow("box /* comment|")]
    [DataRow("box /*/|")]
    [DataRow("box // comment */|")]
    public void Classify_UnclosedComment_HasNoSite(string text)
    {
        var classifier = new CaretClassifier(new CilTokenizer(CilVocabularyBuilder.Vocabulary));
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        Assert.IsFalse(classifier.Classify(text.Remove(caret, 1), caret, false).IsOperand);
    }
}
