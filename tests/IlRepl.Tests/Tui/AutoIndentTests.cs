using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Tests for <see cref="AutoIndent"/>: what a new line starts with, and when a brace steps out.
/// </summary>
[TestClass]
public sealed class AutoIndentTests
{
    /// <summary>
    /// A new line copies the whitespace of the text before the caret, tabs kept.
    /// </summary>
    [TestMethod]
    public void Continuation_CopiesLeadingWhitespace()
    {
        Assert.AreEqual("", AutoIndent.Continuation("ldarg n"));
        Assert.AreEqual("  ", AutoIndent.Continuation("  ldarg n"));
        Assert.AreEqual("\t", AutoIndent.Continuation("\tldarg n"));
        Assert.AreEqual("\t  ", AutoIndent.Continuation("\t  ldarg n"));
        Assert.AreEqual("    ", AutoIndent.Continuation("    "));
    }

    /// <summary>
    /// A line that ends with an opening brace steps the next one in by one unit.
    /// </summary>
    [TestMethod]
    public void Continuation_AfterOpenBrace_AddsUnit()
    {
        Assert.AreEqual("  ", AutoIndent.Continuation(".method int32 F() {"));
        Assert.AreEqual("    ", AutoIndent.Continuation("  .try {"));
        Assert.AreEqual("    ", AutoIndent.Continuation("  } catch Exception {"));
        Assert.AreEqual("  ", AutoIndent.Continuation(".method int32 F() {   "));
        Assert.AreEqual("  ", AutoIndent.Continuation(".method int32 F() { // header"));
    }

    /// <summary>
    /// A brace inside a comment, a string, or a quoted name opens nothing.
    /// </summary>
    [TestMethod]
    public void Continuation_BraceInsideCommentOrLiteral_DoesNotIndent()
    {
        Assert.AreEqual("", AutoIndent.Continuation("nop // {"));
        Assert.AreEqual("", AutoIndent.Continuation("nop /* { */"));
        Assert.AreEqual("", AutoIndent.Continuation("ldstr \"{\""));
        Assert.AreEqual("", AutoIndent.Continuation("ldarg '{'"));
        Assert.AreEqual("", AutoIndent.Continuation("{", inBlockComment: true));
        Assert.AreEqual("  ", AutoIndent.Continuation("*/ .try {", inBlockComment: true));
    }

    /// <summary>
    /// The text before the caret decides, not the whole line: Enter in the middle of a line uses
    /// what is to the left of the caret.
    /// </summary>
    [TestMethod]
    public void Continuation_UsesTextBeforeCaret()
    {
        Assert.AreEqual("  ", AutoIndent.Continuation("  ldarg"));
        Assert.AreEqual("  ", AutoIndent.Continuation(".try {"));
    }

    /// <summary>
    /// A closing brace steps out only from a blank line with at least one unit, outside a comment.
    /// </summary>
    [TestMethod]
    public void ShouldDedent_OnlyFromAnIndentedBlankLine()
    {
        Assert.IsTrue(AutoIndent.ShouldDedent("  "));
        Assert.IsTrue(AutoIndent.ShouldDedent("    "));
        Assert.IsTrue(AutoIndent.ShouldDedent("\t"));
        Assert.IsFalse(AutoIndent.ShouldDedent(""));
        Assert.IsFalse(AutoIndent.ShouldDedent(" "));
        Assert.IsFalse(AutoIndent.ShouldDedent("  nop "));
        Assert.IsFalse(AutoIndent.ShouldDedent("  ", inBlockComment: true));
    }

    /// <summary>
    /// Dedenting takes one unit, or one tab, off the front and leaves a line at column zero alone.
    /// </summary>
    [TestMethod]
    public void Dedent_RemovesOneUnit()
    {
        Assert.AreEqual("  }", AutoIndent.Dedent("    }"));
        Assert.AreEqual("}", AutoIndent.Dedent("  }"));
        Assert.AreEqual("}", AutoIndent.Dedent("}"));
        Assert.AreEqual("}", AutoIndent.Dedent("\t}"));
        Assert.AreEqual("\t}", AutoIndent.Dedent("\t\t}"));
        Assert.AreEqual("", AutoIndent.Dedent("  "));
        Assert.AreEqual(" ", AutoIndent.Dedent(" "));
    }
}
