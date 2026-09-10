using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Qualified matching preserves nested names beneath generic enclosing types.
/// </summary>
[TestClass]
public sealed class NestedTypeCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A typed final arity remains matchable while incomplete and resolves to the intended open definition when accepted.
    /// </summary>
    /// <param name="name">The partial or complete generic metadata name.</param>
    [TestMethod]
    [DataRow("List`")]
    [DataRow("List`1")]
    [DataRow("System.Collections.Generic.List`")]
    [DataRow("System.Collections.Generic.List`1")]
    [DataRow("'List`1'")]
    public async Task Complete_ExplicitFinalArity_BindsAndRuns(string name)
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var line = "ldtoken " + name;
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, line.Length, null, []),
            TestContext.CancellationToken);
        var item = reply.Items.Single(item => !item.Continues && item.InsertText == "List`1");
        var accepted = line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];
        session.AddLine(accepted);
        session.AddLine("call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        Assert.AreEqual(typeof(List<>), session.Run().Value);
    }

    /// <summary>
    /// An enclosing arity cannot truncate the nested name being matched or alter its constructed identity.
    /// </summary>
    /// <param name="name">The qualified partial nested name.</param>
    /// <param name="constructed">Whether arguments already follow the edited name.</param>
    [TestMethod]
    [DataRow("Dictionary`2/Enum", false)]
    [DataRow("System.Collections.Generic.Dictionary`2/Enum", false)]
    [DataRow("dictionary`2/enum", false)]
    [DataRow("System.Collections.Generic.Dictionary`2/Enum", true)]
    public async Task Complete_NestedBelowGenericOwner_BindsAndRuns(string name, bool constructed)
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var prefix = "ldtoken " + name;
        var line = prefix + (constructed ? "<int32, string>" : "");
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, prefix.Length, null, []),
            TestContext.CancellationToken);
        var spelling = "Dictionary`2/Enumerator" + (constructed ? "<int32, string>" : "");
        var item = reply.Items.Single(item => !item.Continues && item.InsertText == spelling);
        var accepted = line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];
        session.AddLine(accepted);
        session.AddLine("call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        var expected = constructed ? typeof(Dictionary<int, string>.Enumerator) : typeof(Dictionary<,>.Enumerator);
        Assert.AreEqual(expected, session.Run().Value);
    }
}
