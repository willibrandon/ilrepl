using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Generic argument completion refuses typed references without hiding their legal type tokens.
/// </summary>
[TestClass]
public sealed class GenericArgumentCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Type and method constructions exclude typed references under keyword, qualified, and modified spellings.
    /// </summary>
    /// <param name="text">The partial construction with a marked caret.</param>
    [TestMethod]
    [DataRow("ldtoken List<typedre|>")]
    [DataRow("call Array::Empty<typedre|>")]
    [DataRow("ldtoken List<System.TypedRefe|>")]
    [DataRow("call Array::Empty<System.TypedRefe|>")]
    [DataRow("ldtoken List<typedre| modopt(int32)>")]
    [DataRow("call Array::Empty<typedre| modopt(int32)>")]
    public async Task Complete_TypedReferenceArgument_IsNotOffered(string text)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var reply = await completer.CompleteAsync(new CompletionRequest([text.Remove(caret, 1)], 0, caret, null, []),
            TestContext.CancellationToken);
        Assert.AreEqual(CompletionKind.TypeArguments, reply.Kind);
        Assert.DoesNotContain(item => item.InsertText == "typedref", reply.Items);
    }

    /// <summary>
    /// The typedref keyword remains available as a token whose runtime identity is System.TypedReference.
    /// </summary>
    [TestMethod]
    public async Task Complete_TypedReferenceToken_Runs()
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        const string line = "ldtoken typedre";
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, line.Length, null, []),
            TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == "typedref");
        session.AddLine(line[..reply.ReplaceStart] + item.InsertText);
        session.AddLine("call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        Assert.AreEqual(typeof(TypedReference), session.Run().Value);
    }
}
