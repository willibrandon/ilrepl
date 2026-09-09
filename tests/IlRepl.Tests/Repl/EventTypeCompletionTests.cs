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
    public async Task Complete_UnfinishedEvent_ExcludesInvalidHandler(string text, string excluded)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var reply = await completer.CompleteAsync(new CompletionRequest(
            [".class public EventHost {", text.Remove(caret, 1)], 1, caret, null, []), TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.InsertText == excluded, reply.Items);
    }

    /// <summary>
    /// Completing a valid handler produces an event with the expected runtime type and callable accessors.
    /// </summary>
    /// <param name="text">The partial event header and marked caret.</param>
    [TestMethod]
    [DataRow(".event System.Act|")]
    [DataRow(".event System.Act| /* kept */")]
    public async Task Complete_DelegateHandler_BindsAndRuns(string text)
    {
        var session = new Session();
        session.AddLine(".class public EventHost {");
        foreach (var name in new[] { "add_Changed", "remove_Changed" })
        {
            session.AddLine(".method public static specialname void " + name + "(Action value) {");
            session.AddLine("ret");
            session.AddLine("}");
        }
        using var completer = new OperandCompleter(session);
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == "Action");
        var declaration = line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];
        session.AddLine(declaration + " Changed {");
        session.AddLine(".addon void EventHost::add_Changed(Action)");
        session.AddLine(".removeon void EventHost::remove_Changed(Action)");
        session.AddLine("}");
        session.AddLine("}");
        Assert.AreEqual(typeof(Action), session.Types.Single().RuntimeType!.GetEvent("Changed")!.EventHandlerType);
        session.AddLine("ldnull");
        session.AddLine("call void EventHost::add_Changed(Action)");
        session.AddLine("ldc.i4.7");
        Assert.AreEqual(7, session.Run().Value);
    }
}
