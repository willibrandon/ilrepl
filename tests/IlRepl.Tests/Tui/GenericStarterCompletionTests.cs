using Hex1b.Documents;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// Generic starters preserve intervening text, existing arguments, and the selected definition through the next edit.
/// </summary>
[TestClass]
public sealed class GenericStarterCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Acceptance reaches the actual opening bracket and the continued argument binds and executes.
    /// </summary>
    /// <param name="text">The partial instruction and marked caret.</param>
    /// <param name="expected">The source after accepting its generic starter.</param>
    [TestMethod]
    [DataRow("call Array::Em| <", "call Array::Empty <")]
    [DataRow("call Array::Em|\t<", "call Array::Empty\t<")]
    [DataRow("call Array::Em|/* < */<", "call Array::Empty/* < */<")]
    [DataRow("call Array::Em| /* kept */<str>", "call Array::Empty /* kept */<str>")]
    [DataRow("ldtoken Lis| <", "ldtoken List <")]
    [DataRow("ldtoken Lis|/* < */<", "ldtoken List/* < */<")]
    [DataRow("ldtoken Dictionary<string, Lis| /* kept */<", "ldtoken Dictionary<string, List /* kept */<")]
    [DataRow("call Array::Empty<Lis| /* kept */<", "call Array::Empty<List /* kept */<")]
    public async Task Accept_SeparatedBracket_PreservesTheContinuation(string text, string expected)
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var state = new PromptState(new PromptHistory(), new CilTokenizer(CilVocabularyBuilder.Vocabulary));
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var original = text.Remove(caret, 1);
        state.SetText(original, caret);
        var reply = await CompleteAsync(completer, state);
        var starter = reply.Items.Single(item => item.Continuation is not null
            && (item.InsertText.StartsWith("Array::Empty", StringComparison.Ordinal)
                || item.InsertText == "List"));
        Apply(state, reply, starter);
        Assert.AreEqual(expected, state.Text);
        Assert.AreEqual(expected.LastIndexOf('<') + 1, state.CaretColumn);
        var anchor = state.Anchors.Snapshot().Single();
        Assert.AreEqual(state.CaretColumn, anchor.End);
        state.Editor.Undo();
        Assert.AreEqual(original, state.Text);
        Assert.AreEqual(caret, state.CaretColumn);
        reply = await CompleteAsync(completer, state);
        starter = reply.Items.Single(item => item.Continuation is not null
            && (item.InsertText.StartsWith("Array::Empty", StringComparison.Ordinal) || item.InsertText == "List"));
        Apply(state, reply, starter);
        Assert.AreEqual(expected, state.Text);
        var existing = state.Text.EndsWith("str>", StringComparison.Ordinal);
        if (existing)
        {
            state.Editor.SetCursorPosition(new DocumentOffset(state.CaretColumn + 3));
        }
        else
        {
            state.Editor.InsertText("str");
        }

        var arguments = await CompleteAsync(completer, state);
        Assert.AreEqual(CompletionKind.TypeArguments, arguments.Kind);
        Assert.HasCount(1, arguments.Owners);
        Apply(state, arguments, arguments.Items.Single(item => item.InsertText == "string"));
        if (existing)
        {
            state.Editor.SetCursorPosition(new DocumentOffset(state.Text.Length));
        }
        else
        {
            state.Editor.InsertText(expected.StartsWith("ldtoken Dictionary", StringComparison.Ordinal)
                || expected.StartsWith("call Array::Empty<List", StringComparison.Ordinal) ? ">>" : ">");
        }

        if (expected.StartsWith("call", StringComparison.Ordinal))
        {
            var signature = await CompleteAsync(completer, state);
            Assert.HasCount(1, signature.Items);
            Apply(state, signature, signature.Items[0]);
        }

        session.AddLine(state.Text);
        session.AddLine("pop");
        session.AddLine("ldc.i4.7");
        Assert.AreEqual(7, session.Run().Value);
        state.Anchors.Dispose();
    }

    private Task<CompletionReply> CompleteAsync(OperandCompleter completer, PromptState state) =>
        completer.CompleteAsync(new CompletionRequest([state.Text], 0, state.CaretColumn, null, state.Anchors.Snapshot()),
            TestContext.CancellationToken);

    private static void Apply(PromptState state, CompletionReply reply, CompletionItem item)
    {
        var edit = new CompletionEdit(new DocumentRange(new DocumentOffset(reply.ReplaceStart),
            new DocumentOffset(reply.ReplaceStart + reply.ReplaceLength)), item.InsertText, item.CaretOffset);
        edit.Apply(state);
        if (item.Continuation is { } token)
        {
            state.Anchors.Add(reply.ReplaceStart, state.CaretColumn, token);
        }
    }
}
