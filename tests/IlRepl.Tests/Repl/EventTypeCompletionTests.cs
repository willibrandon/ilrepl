using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Event completion validates the entire handler type before an event name is present.
/// </summary>
[TestClass]
public sealed class EventTypeCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Retained arrays, pointers, and managed references cannot turn a delegate candidate into an invalid event type.
    /// </summary>
    /// <param name="text">The partial event header and marked caret.</param>
    /// <param name="excluded">The insertion whose completed type is invalid.</param>
    [TestMethod]
    [DataRow(".event System.Act|[]", "Action")]
    [DataRow(".event System.Act|[,]", "Action")]
    [DataRow(".event System.Act|[0...]", "Action")]
    [DataRow(".event System.Act|*", "Action")]
    [DataRow(".event System.Act|&", "Action")]
    [DataRow(".event System.Act| /* kept */[]", "Action")]
    [DataRow("/* before */ .event System.Act| /* kept */[]", "Action")]
    [DataRow(".event System.Act|ion<int32>[]", "Action<int32>")]
    [DataRow(".event System.Action<in|>[] Changed {", "int32")]
    [DataRow(".event System.Action<in|>[]", "int32")]
    [DataRow(".event System.Action<in|>[,]", "int32")]
    [DataRow(".event System.Action<in|>[0...]", "int32")]
    [DataRow(".event System.Action<in|>*", "int32")]
    [DataRow(".event System.Action<in|>&", "int32")]
    [DataRow(".event System.Action<List<in|>>[]", "int32")]
    [DataRow("/* before */ .event System.Action<in|> /* kept */[]", "int32")]
    [DataRow(".event System.Action<Act|>[] Changed {", "Action")]
    [DataRow(".event System.Action<List<Act|>>[]", "Action")]
    [DataRow("/* before */ .event System.Action<Act|> /* kept */[]", "Action")]
    public async Task Complete_UnfinishedEvent_ExcludesInvalidHandler(string text, string excluded)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var reply = await completer.CompleteAsync(new CompletionRequest(
            [".class public EventHost {", text.Remove(caret, 1)], 1, caret, null, []), TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.InsertText == excluded, reply.Items);
    }

    /// <summary>
    /// Generic arguments remain available while the delegate's type or declaration is still unfinished.
    /// </summary>
    /// <param name="text">The event header and marked caret.</param>
    [TestMethod]
    [DataRow(".event System.Action<in|")]
    [DataRow(".event System.Action<in|>")]
    [DataRow(".event System.Action<in|> Changed {")]
    [DataRow(".event System.Action<List<in|>>")]
    [DataRow(".event System.Action<List<in|>")]
    [DataRow(".event System.Action<in|,>")]
    [DataRow(".event System.Action<int32, in|>")]
    public async Task Complete_GenericDelegate_OffersArgument(string text)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var reply = await completer.CompleteAsync(new CompletionRequest(
            [".class public EventHost {", text.Remove(caret, 1)], 1, caret, null, []), TestContext.CancellationToken);
        Assert.Contains(item => item.InsertText == "int32", reply.Items);
    }

    /// <summary>
    /// Completing a valid handler produces an event with the expected runtime type and callable accessors.
    /// </summary>
    /// <param name="text">The partial event header and marked caret.</param>
    /// <param name="handler">The accessor parameter type.</param>
    /// <param name="insertion">The type component to accept.</param>
    /// <param name="expected">The runtime handler type.</param>
    [TestMethod]
    [DataRow(".event System.Act|", "Action", "Action", typeof(Action))]
    [DataRow(".event System.Act| /* kept */", "Action", "Action", typeof(Action))]
    [DataRow(".event System.Action<in|>", "Action<int32>", "int32", typeof(Action<int>))]
    [DataRow(".event System.Action<List<in|>>", "Action<List<int32>>", "int32", typeof(Action<List<int>>))]
    [DataRow(".event System.Action<int32, in|>", "Action<int32, int32>", "int32", typeof(Action<int, int>))]
    public async Task Complete_DelegateHandler_BindsAndRuns(string text, string handler, string insertion, Type expected)
    {
        var session = new Session();
        session.AddLine(".class public EventHost {");
        foreach (var name in new[] { "add_Changed", "remove_Changed" })
        {
            session.AddLine(".method public static specialname void " + name + "(" + handler + " value) {");
            session.AddLine("ret");
            session.AddLine("}");
        }
        using var completer = new OperandCompleter(session);
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == insertion);
        var declaration = line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];
        session.AddLine(declaration + " Changed {");
        session.AddLine(".addon void EventHost::add_Changed(" + handler + ")");
        session.AddLine(".removeon void EventHost::remove_Changed(" + handler + ")");
        session.AddLine("}");
        session.AddLine("}");
        Assert.AreEqual(expected, session.Types.Single().RuntimeType!.GetEvent("Changed")!.EventHandlerType);
        session.AddLine("ldnull");
        session.AddLine("call void EventHost::add_Changed(" + handler + ")");
        session.AddLine("ldc.i4.7");
        Assert.AreEqual(7, session.Run().Value);
    }
}
