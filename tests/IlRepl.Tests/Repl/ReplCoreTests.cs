using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Tests for <see cref="ReplCore"/>: lines in, transcript out.
/// </summary>
[TestClass]
public sealed class ReplCoreTests
{
    private static string Plain(ReplCore core) => string.Join("\n", core.Transcript.Lines.Select(l => l.PlainText));

    /// <summary>
    /// An instruction echoes the stack and ret prints the value with its type.
    /// </summary>
    [TestMethod]
    public void Handle_InstructionAndRet_EchoesStackAndResult()
    {
        var core = new ReplCore();
        core.Handle("ldc.i4 6");
        core.Handle("ldc.i4 7");
        core.Handle("mul");
        core.Handle("ret");

        var text = Plain(core);
        Assert.Contains("┊ [int32, int32] ◂ top", text);
        Assert.Contains("= 42 : int32", text);
        Assert.AreEqual(2, core.CellNumber);
        Assert.AreEqual("il[2]> ", core.Prompt);
    }

    /// <summary>
    /// An empty line runs a non-empty cell and does nothing otherwise.
    /// </summary>
    [TestMethod]
    public void Handle_EmptyLine_RunsCell()
    {
        var core = new ReplCore();
        core.Handle("");
        Assert.AreEqual(1, core.CellNumber);
        core.Handle("ldstr \"x\"");
        core.Handle("");
        Assert.Contains("= \"x\" : string", Plain(core));
    }

    /// <summary>
    /// Errors are reported as error lines and do not consume the cell.
    /// </summary>
    [TestMethod]
    public void Handle_Error_ReportsAndKeepsCell()
    {
        var core = new ReplCore();
        core.Handle("ldc.i4 1");
        var result = core.Handle("lcd.i4 2");
        Assert.IsFalse(result.Succeeded);
        Assert.Contains("did you mean 'ldc.i4'", Plain(core));
        Assert.AreEqual("[int32]", core.Status.Stack);
    }

    /// <summary>
    /// A mistyped method name gets the nearest member of the type, and the cell keeps its lines.
    /// </summary>
    [TestMethod]
    public void Handle_MistypedMethod_ShowsDidYouMean()
    {
        var core = new ReplCore();
        core.Handle("ldstr \"a\"");
        core.Handle("ldstr \"b\"");
        var result = core.Handle("call string String::Concta(string, string)");
        Assert.IsFalse(result.Succeeded);
        Assert.Contains("no method 'Concta' on string (did you mean 'Concat'?)", Plain(core));
        Assert.AreEqual("[string, string]", core.Status.Stack);
        Assert.IsTrue(core.Handle("call string String::Concat(string, string)").Succeeded);
    }

    /// <summary>
    /// ret inside a cell is emitted while a forward label is pending.
    /// </summary>
    [TestMethod]
    public void Handle_RetWithPendingLabel_StaysInline()
    {
        var core = new ReplCore();
        core.Handle("ldc.i4 1");
        core.Handle("brtrue SKIP");
        core.Handle("ldstr \"no\"");
        core.Handle("ret");
        Assert.AreEqual(1, core.CellNumber);
        core.Handle("SKIP: ldstr \"yes\"");
        core.Handle("ret");
        Assert.Contains("= \"yes\" : string", Plain(core));
    }

    /// <summary>
    /// Console output from the cell lands in the transcript as output lines.
    /// </summary>
    [TestMethod]
    public void Handle_CellOutput_BecomesOutputLines()
    {
        var core = new ReplCore();
        core.Handle("ldstr \"printed\"");
        core.Handle("call void Console::WriteLine(string)");
        core.Handle("ret");
        Assert.Contains(l => l.Kind == LineKind.Output && l.PlainText == "printed", core.Transcript.Lines);
    }

    /// <summary>
    /// Thrown exceptions are reported with their type.
    /// </summary>
    [TestMethod]
    public void Handle_Throw_ReportsException()
    {
        var core = new ReplCore();
        core.Handle("ldc.i4 1");
        core.Handle("ldc.i4 0");
        core.Handle("div");
        var result = core.Handle("ret");
        Assert.IsFalse(result.Succeeded);
        Assert.Contains("threw System.DivideByZeroException", Plain(core));
    }

    /// <summary>
    /// Every command produces a transcript line and the ones with state change it.
    /// </summary>
    [TestMethod]
    public void Handle_Commands_Work()
    {
        var core = new ReplCore();
        core.Handle(".help");
        Assert.Contains("member references", Plain(core));

        core.Handle(".ops ldelem");
        Assert.Contains("ldelem.ref", Plain(core));

        core.Handle("ldc.i4 1");
        core.Handle("ldc.i4 2");
        core.Handle(".show");
        Assert.Contains("001  ldc.i4 2", Plain(core));

        core.Handle(".undo");
        Assert.AreEqual("[int32]", core.Status.Stack);

        core.Handle(".time on");
        core.Handle("ret");
        Assert.Contains(l => l.Kind == LineKind.Result && (l.PlainText.Contains("ms", StringComparison.Ordinal) || l.PlainText.Contains("µs", StringComparison.Ordinal)), core.Transcript.Lines);

        core.Handle(".quiet on");
        core.Handle("ldc.i4 3");
        Assert.AreNotEqual(LineKind.Stack, core.Transcript.Lines[^1].Kind);

        core.Handle(".clear");
        Assert.IsTrue(core.Status.CellIsEmpty);

        core.Handle(".il");
        Assert.Contains(".method public static object Run()", Plain(core));

        var quit = core.Handle(".quit");
        Assert.IsTrue(quit.QuitRequested);

        var bad = core.Handle(".bogus");
        Assert.IsFalse(bad.Succeeded);
    }

    /// <summary>
    /// Loading an assembly makes its types resolve, and .assemblies lists it.
    /// </summary>
    [TestMethod]
    public void Handle_Load_MakesTypesResolve()
    {
        var core = new ReplCore();
        var result = core.Handle(".load " + SampleHost.Samples.GreeterDll);
        Assert.IsTrue(result.Succeeded);
        core.Handle(".assemblies");
        Assert.Contains("Greeter", Plain(core));
        core.Handle("ldstr \"world\"");
        core.Handle("call string Greeter.Hello::Say(string)");
        core.Handle("ret");
        Assert.Contains("= \"Hello, world!\" : string", Plain(core));
    }

    /// <summary>
    /// The status reflects the cell.
    /// </summary>
    [TestMethod]
    public void Status_ReflectsCell()
    {
        var core = new ReplCore();
        core.Handle(".locals init (int32 i)");
        core.Handle(".try {");
        core.Handle("ldc.i4 1");
        var status = core.Status;
        Assert.AreEqual(1, status.Locals);
        Assert.AreEqual(1, status.OpenBlocks);
        Assert.AreEqual(1, status.Instructions);
        Assert.AreEqual("[int32]", status.Stack);
        Assert.IsFalse(status.CellIsEmpty);
    }

    /// <summary>
    /// A line that is only a comment is not the blank line that runs the cell.
    /// </summary>
    [TestMethod]
    public void Handle_CommentOnlyLine_WithPendingCell_DoesNotRun()
    {
        var core = new ReplCore();
        core.Handle("ldc.i4 1");
        Assert.IsTrue(core.Handle("// note").Succeeded);
        Assert.IsTrue(core.Handle("/* aside */").Succeeded);
        Assert.AreEqual(1, core.Status.StackDepth);
        Assert.AreEqual(1, core.CellNumber);
        Assert.DoesNotContain("= 1", Plain(core));
        core.Handle("");
        Assert.Contains("= 1 : int32", Plain(core));
    }

    /// <summary>
    /// A comment inside an open method or class is ignored rather than reported as a blank line
    /// that cannot run while the block is open.
    /// </summary>
    [TestMethod]
    public void Handle_CommentOnlyLine_InsideBlock_Succeeds()
    {
        var core = new ReplCore();
        core.Handle(".method int32 F() {");
        Assert.IsTrue(core.Handle("// body follows").Succeeded);
        Assert.AreEqual("F", core.Status.OpenMethod);
        core.Handle("ldc.i4 1");
        core.Handle("ret");
        core.Handle("}");
        core.Handle(".class public C {");
        Assert.IsTrue(core.Handle("  // a field").Succeeded);
        Assert.AreEqual("C", core.Status.OpenType);
        Assert.DoesNotContain("error", Plain(core));
    }

    /// <summary>
    /// A blank line inside an open block comment is part of the comment, and text after the
    /// closing delimiter is handled.
    /// </summary>
    [TestMethod]
    public void Handle_BlankLineInsideBlockComment_DoesNotRun()
    {
        var core = new ReplCore();
        core.Handle("ldc.i4 1");
        core.Handle("/*");
        Assert.IsTrue(core.Handle("").Succeeded);
        Assert.IsTrue(core.Handle("ret").Succeeded);
        Assert.AreEqual(1, core.Status.StackDepth);
        Assert.AreEqual(1, core.CellNumber);
        core.Handle("*/ ldc.i4 2");
        Assert.AreEqual(2, core.Status.StackDepth);
    }

    /// <summary>
    /// A command inside a block comment is text in a comment, not a command.
    /// </summary>
    [TestMethod]
    public void Handle_ResetInsideComment_IsIgnored()
    {
        var core = new ReplCore();
        core.Handle(".method int32 F() {");
        core.Handle("/* open");
        Assert.IsTrue(core.Handle(".reset").Succeeded);
        Assert.IsTrue(core.Handle(".quit").Succeeded);
        core.Handle("*/");
        Assert.AreEqual("F", core.Status.OpenMethod);
        core.Handle("ldc.i4 1");
        core.Handle("ret");
        core.Handle("}");
        Assert.AreEqual(1, core.Status.Methods);
        Assert.DoesNotContain("cleared", Plain(core));
    }

    /// <summary>
    /// A comment left open at the top level comments out every line until one closes it.
    /// </summary>
    [TestMethod]
    public void Handle_UnterminatedTopLevelComment_SwallowsFollowingLines()
    {
        var core = new ReplCore();
        core.Handle("/* open");
        core.Handle("ldc.i4 1");
        core.Handle("ret");
        Assert.AreEqual(0, core.Status.StackDepth);
        Assert.AreEqual(1, core.CellNumber);
        core.Handle("*/ ldc.i4 3");
        core.Handle("ret");
        Assert.Contains("= 3 : int32", Plain(core));
    }

    /// <summary>
    /// The echo keeps the line as typed, comment and all; the session keeps the text.
    /// </summary>
    [TestMethod]
    public void Handle_EchoesRawLine_StoresText()
    {
        var core = new ReplCore();
        core.Handle("  ldc.i4 1 // one");
        Assert.AreEqual("il[1]>   ldc.i4 1 // one", core.Transcript.Lines[0].PlainText);
        Assert.AreEqual("ldc.i4 1", core.Session.BodyLines[0]);
    }

    /// <summary>
    /// The echo is lit by the tokenizer after the prompt, and its text is the line as typed.
    /// </summary>
    [TestMethod]
    public void Handle_EchoLine_IsTokenized()
    {
        var core = new ReplCore();
        core.Handle("ldc.i4 6");
        var echo = core.Transcript.Lines[0];
        Assert.AreEqual(LineKind.Input, echo.Kind);
        Assert.AreEqual(SpanStyle.Prompt, echo.Spans[0].Style);
        Assert.AreEqual(new TranscriptSpan("ldc.i4", SpanStyle.Opcode), echo.Spans[1]);
        Assert.AreEqual(new TranscriptSpan(" ", SpanStyle.Input), echo.Spans[2]);
        Assert.AreEqual(new TranscriptSpan("6", SpanStyle.Number), echo.Spans[3]);
        Assert.AreEqual("il[1]> ldc.i4 6", echo.PlainText);
    }

    /// <summary>
    /// A listing keeps its columns: offset, instruction padded to a fixed width, stack.
    /// </summary>
    [TestMethod]
    public void Handle_Show_KeepsColumnsAndColoursMembers()
    {
        var core = new ReplCore();
        core.Handle("ldc.i4 -3");
        core.Handle("call int32 Math::Abs(int32)");
        core.Handle(".show");
        var rows = core.Transcript.Lines.Where(l => l.Kind == LineKind.Listing).ToList();
        Assert.AreEqual("  000  ldc.i4 -3                                [int32]", rows[0].PlainText);
        Assert.AreEqual(47, rows[1].PlainText.IndexOf(" [int32]", StringComparison.Ordinal));
        Assert.Contains(new TranscriptSpan("Abs", SpanStyle.Member), rows[1].Spans);
        Assert.AreEqual(SpanStyle.Dim, rows[1].Spans[0].Style);
        Assert.AreEqual(SpanStyle.Dim, rows[1].Spans[^1].Style);
    }

    /// <summary>
    /// The block rows of a listing are lit like the block lines typed at the prompt.
    /// </summary>
    [TestMethod]
    public void Handle_Show_BlockRowsAreTokenized()
    {
        var core = new ReplCore();
        core.Handle(".try {");
        core.Handle("nop");
        core.Handle("leave END");
        core.Handle("} catch [System.Runtime]System.Exception {");
        core.Handle("pop");
        core.Handle("leave END");
        core.Handle("}");
        core.Handle("END: nop");
        core.Handle(".show");
        var rows = core.Transcript.Lines.Where(l => l.Kind == LineKind.Listing).ToList();
        var tryRow = rows.Single(r => r.PlainText.Contains(".try {", StringComparison.Ordinal));
        Assert.Contains(new TranscriptSpan(".try", SpanStyle.Directive), tryRow.Spans);
        Assert.Contains(new TranscriptSpan("{", SpanStyle.Punctuation), tryRow.Spans);
        var catchRow = rows.Single(r => r.PlainText.Contains("catch", StringComparison.Ordinal));
        Assert.Contains(new TranscriptSpan("catch", SpanStyle.Keyword), catchRow.Spans);
        Assert.Contains(new TranscriptSpan("Exception", SpanStyle.Type), catchRow.Spans);
        Assert.Contains(new TranscriptSpan("}", SpanStyle.Punctuation), catchRow.Spans);
        Assert.AreEqual("} catch Exception {", catchRow.PlainText.Trim());
        var label = rows.Single(r => r.PlainText == "END:");
        Assert.AreEqual(SpanStyle.Label, label.Spans[0].Style);
    }

    /// <summary>
    /// Nothing the REPL renders as ILAsm is an error token.
    /// </summary>
    [TestMethod]
    public void Handle_Il_HasNoErrorSpan()
    {
        var core = new ReplCore();
        foreach (var line in new[] { ".locals init (int32 i)", ".method int32 F(int32 n) {", ".locals init (int32 r)", ".try {", "ldarg n", "stloc r", "leave END", "} catch [System.Runtime]System.Exception {", "pop", "ldc.i4 0", "stloc r", "leave END", "}", "END: ldloc r", "ret", "}", "ldc.i4 1", "stloc i", ".il" })
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line);
        }

        var listing = core.Transcript.Lines.Where(l => l.Kind == LineKind.Listing).ToList();
        Assert.IsGreaterThan(10, listing.Count);
        Assert.Contains(l => l.Spans.Contains(new TranscriptSpan(".assembly", SpanStyle.Directive)), listing);
        Assert.Contains(l => l.Spans.Contains(new TranscriptSpan("managed", SpanStyle.Keyword)), listing);
        Assert.DoesNotContain(l => l.Spans.Any(s => s.Style == SpanStyle.Error), listing, string.Join("\n", listing.Where(l => l.Spans.Any(s => s.Style == SpanStyle.Error)).Select(l => l.PlainText)));
    }

    /// <summary>
    /// A header whose brace comes on the next line has opened nothing yet: the depth counts
    /// braces the session has seen, so an editor adding the brace itself reaches zero at the close.
    /// </summary>
    [TestMethod]
    public void Handle_HeaderWithoutBrace_OpenDepthCountsFromTheBrace()
    {
        var core = new ReplCore();
        Assert.IsTrue(core.Handle(".method int32 One()").Succeeded);
        Assert.AreEqual("One", core.Status.OpenMethod);
        Assert.AreEqual(0, core.Status.OpenDepth, "the brace has not been seen");
        Assert.IsTrue(core.Handle("{").Succeeded);
        Assert.AreEqual(1, core.Status.OpenDepth);
        Assert.IsTrue(core.Handle("ldc.i4 1").Succeeded);
        Assert.IsTrue(core.Handle("ret").Succeeded);
        Assert.IsTrue(core.Handle("}").Succeeded);
        Assert.AreEqual(0, core.Status.OpenDepth);
        Assert.IsNull(core.Status.OpenMethod);

        Assert.IsTrue(core.Handle(".class C").Succeeded);
        Assert.AreEqual(0, core.Status.OpenDepth);
        Assert.IsTrue(core.Handle("{").Succeeded);
        Assert.AreEqual(1, core.Status.OpenDepth);
        Assert.IsTrue(core.Handle(".method public static int32 M()").Succeeded);
        Assert.AreEqual(1, core.Status.OpenDepth, "the member's brace has not been seen");
        Assert.IsTrue(core.Handle("{").Succeeded);
        Assert.AreEqual(2, core.Status.OpenDepth);
        Assert.IsTrue(core.Handle("ldc.i4 2").Succeeded);
        Assert.IsTrue(core.Handle("ret").Succeeded);
        Assert.IsTrue(core.Handle("}").Succeeded);
        Assert.AreEqual(1, core.Status.OpenDepth);
        Assert.IsTrue(core.Handle("}").Succeeded);
        Assert.AreEqual(0, core.Status.OpenDepth);
    }

    /// <summary>
    /// A refused line changes nothing, the block comment it opened included, so the corrected
    /// line that follows is read as code.
    /// </summary>
    [TestMethod]
    public void Handle_RefusedLine_LeavesNoCommentOpen()
    {
        var core = new ReplCore();
        Assert.IsFalse(core.Handle("lcd.i4 2 /*").Succeeded);
        Assert.IsFalse(core.Status.Mark.InBlockComment, "the refused line's comment is forgotten with it");
        Assert.IsTrue(core.Handle("ldc.i4 2").Succeeded);
        Assert.AreEqual(1, core.Status.Instructions);

        Assert.IsTrue(core.Handle("ldc.i4 3 /* still open").Succeeded);
        Assert.IsTrue(core.Status.Mark.InBlockComment, "an accepted line's comment stays open");
    }
}
