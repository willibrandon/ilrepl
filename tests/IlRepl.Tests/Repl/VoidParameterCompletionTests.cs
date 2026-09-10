using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Unfinished method declarations exclude void parameters while retaining void returns and pointer parameters.
/// </summary>
[TestClass]
public sealed class VoidParameterCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Parameter completion excludes void before the surrounding method header is complete.
    /// </summary>
    /// <param name="text">The unfinished declaration with a caret marker.</param>
    [TestMethod]
    [DataRow(".method void M(vo|")]
    [DataRow(".method void M(System.Voi|")]
    [DataRow(".method void M(int32 first, vo|")]
    [DataRow(".method void M([in] vo|")]
    [DataRow(".method void M(vo|[]")]
    [DataRow(".method void M(vo|&")]
    [DataRow(".method void M(vo| modopt(System.Runtime.CompilerServices.IsReadOnlyAttribute)")]
    [DataRow(".method method void *(vo|")]
    [DataRow(".method void M(method void *(vo|")]
    public async Task Complete_UnfinishedParameter_ExcludesVoid(string text)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.InsertText == "void", reply.Items);
    }

    /// <summary>
    /// Class member headers apply the same parameter restriction as session methods.
    /// </summary>
    [TestMethod]
    public async Task Complete_UnfinishedMemberParameter_ExcludesVoid()
    {
        using var completer = new OperandCompleter(new Session());
        var prefix = ".method public void M(vo";
        var reply = await completer.CompleteAsync(new CompletionRequest([".class public VoidHost {", prefix], 1, prefix.Length, null, []),
            TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.InsertText == "void", reply.Items);
    }

    /// <summary>
    /// Completing void returns and pointer parameters still produces methods that close and execute.
    /// </summary>
    /// <param name="text">The valid declaration with a caret marker.</param>
    /// <param name="remaining">The rest of an unfinished declaration, added after accepting completion.</param>
    /// <param name="hasParameter">Whether a null pointer argument is needed to invoke the method.</param>
    [TestMethod]
    [DataRow(".method vo| M() {", "", false)]
    [DataRow(".method void M(vo|* value) {", "", true)]
    [DataRow(".method void M(method vo| *() callback) {", "", true)]
    [DataRow(".method void M(method vo|", " *() callback) {", true)]
    public async Task Complete_VoidReturnAndPointerParameter_BindsAndRuns(string text, string remaining, bool hasParameter)
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        Assert.Contains(item => item.InsertText == "void", reply.Items,
            new CaretClassifier(new CilTokenizer(CilVocabularyBuilder.Vocabulary)).Classify(line, caret, false).ToString());
        var item = reply.Items.Single(item => item.InsertText == "void");
        session.AddLine(line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..] + remaining);
        session.AddLine("ret");
        session.AddLine("}");
        if (hasParameter)
        {
            session.AddLine("ldc.i4.0");
            session.AddLine("conv.u");
        }

        session.AddLine("call M");
        session.AddLine("ldc.i4.7");
        Assert.AreEqual(7, session.Run().Value);
    }
}
