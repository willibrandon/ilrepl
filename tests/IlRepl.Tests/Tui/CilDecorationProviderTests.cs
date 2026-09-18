using Hex1b;
using Hex1b.Documents;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Preserves lexical coloring while adjacent viewport rows reuse the preceding document's comment state.
/// </summary>
[TestClass]
public sealed class CilDecorationProviderTests
{
    /// <summary>
    /// Separate documents with equal versions never reuse one another's lexical state or rendered spans.
    /// </summary>
    [TestMethod]
    public void ReplacementDocument_ResetsCommentStateAndDecorations()
    {
        var provider = new CilDecorationProvider(new CilTokenizer(CilVocabularyBuilder.Vocabulary));
        var comment = new Hex1bDocument("/*\nnop");
        var code = new Hex1bDocument("//\nnop");
        Assert.AreEqual(comment.Version, code.Version);
        AssertColor(provider, comment, 2, SpanStyle.Comment);
        AssertColor(provider, code, 2, SpanStyle.Opcode);
    }

    /// <summary>
    /// Scrolling, caret movement, source edits, and an inherited comment preserve exact colors below a long prefix.
    /// </summary>
    [TestMethod]
    public void LongPrefix_TracksEditsAndInheritedCommentState()
    {
        var provider = new CilDecorationProvider(new CilTokenizer(CilVocabularyBuilder.Vocabulary));
        var source = string.Join('\n', Enumerable.Repeat("nop", 2000).Prepend("/*").Append("*/").Append("ret"));
        var document = new Hex1bDocument(source);
        for (var line = 1980; line <= 2001; line++)
            AssertColor(provider, document, line, SpanStyle.Comment);
        AssertColor(provider, document, 2003, SpanStyle.Opcode);
        provider.Caret = new DocumentPosition(1980, 2);
        AssertColor(provider, document, 1980, SpanStyle.Comment);

        document.Apply(new ReplaceOperation(new DocumentRange(new DocumentOffset(0), new DocumentOffset(2)), "//"));
        AssertColor(provider, document, 2000, SpanStyle.Opcode);
        provider.CommentOpenAtStart = true;
        AssertColor(provider, document, 2000, SpanStyle.Comment);
        AssertColor(provider, document, 2003, SpanStyle.Opcode);
    }

    /// <summary>
    /// Comment-looking text in a quoted operand or line comment does not alter subsequent viewport rows.
    /// </summary>
    /// <param name="prefix">A complete line that contains an inert block-comment delimiter.</param>
    [TestMethod]
    [DataRow("ldstr \"/*\"")]
    [DataRow("// /*")]
    public void Prefix_RespectsQuotedAndLineCommentDelimiters(string prefix)
    {
        var provider = new CilDecorationProvider(new CilTokenizer(CilVocabularyBuilder.Vocabulary));
        var document = new Hex1bDocument(prefix + "\nnop\nret");
        AssertColor(provider, document, 3, SpanStyle.Opcode);
        AssertColor(provider, document, 2, SpanStyle.Opcode);
    }

    /// <summary>
    /// Caret-dependent opcode errors and replaced diagnostics refresh without changing the document's lexical prefix.
    /// </summary>
    [TestMethod]
    public void CaretAndDiagnostics_RefreshDecorationsForUnchangedDocument()
    {
        var provider = new CilDecorationProvider(new CilTokenizer(CilVocabularyBuilder.Vocabulary));
        var document = new Hex1bDocument(string.Join('\n', Enumerable.Repeat("nop", 2000).Append("ldc.i")));
        var version = document.Version;
        provider.Caret = new DocumentPosition(2001, 6);
        Assert.IsEmpty(provider.GetDecorations(2001, 2001, document));
        provider.Caret = new DocumentPosition(2001, 1);
        Assert.AreEqual(UnderlineStyle.Curly, provider.GetDecorations(2001, 2001, document).Single().Decoration.UnderlineStyle);
        provider.Caret = new DocumentPosition(2001, 6);
        Assert.IsEmpty(provider.GetDecorations(2001, 2001, document));

        AssertColor(provider, document, 2000, SpanStyle.Opcode);
        provider.Diagnostics = [new AnalysisDiagnostic("TEST", AnalysisDiagnosticKind.Error, "current error",
            new AnalysisLocation("document", 1999, 0, 3), [])];
        var withError = provider.GetDecorations(2000, 2000, document);
        Assert.HasCount(2, withError);
        Assert.Contains(span => span.Start == new DocumentPosition(2000, 1) && span.End == new DocumentPosition(2000, 4)
            && span.Decoration.UnderlineStyle == UnderlineStyle.Curly, withError);
        provider.Diagnostics = [];
        AssertColor(provider, document, 2000, SpanStyle.Opcode);
        Assert.AreEqual(version, document.Version);
    }

    private static void AssertColor(CilDecorationProvider provider, IHex1bDocument document, int line, SpanStyle style) =>
        Assert.AreEqual(SpanPalette.Color(style), provider.GetDecorations(line, line, document).Single().Decoration.Foreground);
}
