using IlRepl.Engine.Binding;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Quoted method references use the same alias parsing during preparation, draft analysis, and execution.
/// </summary>
[TestClass]
public sealed class QuotedEditReferenceTests
{
    /// <summary>
    /// Supplies cancellation for real draft analysis.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Spaces, alias words, and escaped quotes inside CIL identifiers do not split the selected method reference.
    /// </summary>
    /// <param name="method">The quoted CIL method name.</param>
    /// <param name="alias">The optional alias suffix.</param>
    /// <param name="expected">The prepared edit name.</param>
    [TestMethod]
    [DataRow("Read as Text", "", "Read_as_Text_Edit")]
    [DataRow("Read as Text", " as Copy", "Copy")]
    [DataRow("Read as Text as Value", "", "Read_as_Text_as_Value_Edit")]
    [DataRow("Read\\' as Text", "", "Read__as_Text_Edit")]
    [DataRow("Read as Text", "\tas\tCopy", "Copy")]
    public void Handle_QuotedReferencePreparesAnalyzesAndExecutes(string method, string alias, string expected)
    {
        var session = IlLines.Load(".class public 'Owner as Type' {",
            ".method public static int32 '" + method + "'() { ldc.i4.s 42; ret }", "}");
        var core = new ReplCore(session, new ReplOptions());
        var reference = "int32 'Owner as Type'::'" + method + "'()";
        var prepared = core.Handle(".edit " + reference + alias);
        Assert.IsTrue(prepared.Succeeded, Transcript(core));
        Assert.IsNotNull(prepared.EditDocument);
        Assert.AreEqual(expected, prepared.EditDocument.Name);
        Assert.AreEqual(reference, prepared.EditDocument.OriginalReference);
        var source = prepared.EditDocument.Source;
        source = ".edit " + reference + alias + " {" + source[source.IndexOf('\n')..];
        source = source.Replace("ldc.i4.s 42", "ldc.i4.s 43", StringComparison.Ordinal);
        session = IlLines.Load(".class public 'Owner as Type' {",
            ".method public static int32 '" + method + "'() { ldc.i4.s 42; ret }", "}");
        core = new ReplCore(session, new ReplOptions());
        var lines = source.Split('\n');
        using (var editing = new EditingSession(session))
        {
            var view = editing.Speculate(lines, lines.Length, cancellationToken: TestContext.CancellationToken);
            Assert.IsEmpty(view.SkippedLines, string.Join('\n', view.SkippedLines));
            Assert.IsNull(view.OpenMethod);
        }

        foreach (var line in lines)
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n" + Transcript(core));
        }

        var edit = session.Edits.Single();
        Assert.AreEqual(42, edit.OriginalMethod.Invoke(null, null));
        Assert.IsTrue(core.Handle("call " + expected).Succeeded, Transcript(core));
        Assert.AreEqual(43, session.Run().Value);
    }

    private static string Transcript(ReplCore core) => string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
}
