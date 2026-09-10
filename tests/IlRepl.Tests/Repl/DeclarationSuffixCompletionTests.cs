using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// An unfinished declaration suffix leaves earlier types completable while complete declarations still validate.
/// </summary>
[TestClass]
public sealed class DeclarationSuffixCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An unfinished implementation attribute does not hide earlier method types in either declaration scope.
    /// </summary>
    /// <param name="text">The method header and marked caret.</param>
    [TestMethod]
    [DataRow(".method void M(List<in|> value) cil man")]
    [DataRow(".method void M(List<in|> value) cil noinline")]
    [DataRow(".method void M(List<in|> value) cil aggressiveopt")]
    [DataRow(".method int|32 M() cil man")]
    [DataRow(".method in| M() cil man")]
    [DataRow(".method in| M() cil managed")]
    [DataRow(".method List<in|> M() cil man")]
    [DataRow(".method void M<(IEnumerable<in|>) T>() cil man")]
    [DataRow("/* before */ .method void M(List<in|> value) cil /* kept */ man")]
    public async Task Complete_UnfinishedMethodTrailer_OffersEarlierType(string text)
    {
        foreach (var member in new[] { false, true })
        {
            var session = new Session();
            if (member)
            {
                session.AddLine(".class public Host {");
            }

            using var completer = new OperandCompleter(session);
            var reply = await Complete(completer, text);
            Assert.Contains(item => item.InsertText == "int32", reply.Items);
        }
    }

    /// <summary>
    /// Completing a method's earlier type and finishing its trailer produces an executable method.
    /// </summary>
    /// <param name="member">Whether the method belongs to a session class.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_MethodTrailer_BindsAndRuns(bool member)
    {
        var session = new Session();
        if (member)
        {
            session.AddLine(".class public Host {");
        }

        using var completer = new OperandCompleter(session);
        const string text = ".method public static int32 M(List<in|> value) cil man";
        var reply = await Complete(completer, text);
        session.AddLine(Accept(text, reply, "int32") + "aged {");
        session.AddLine("ldc.i4.7");
        session.AddLine("ret");
        session.AddLine("}");
        if (member)
        {
            session.AddLine("}");
        }

        session.AddLine("ldnull");
        session.AddLine("call int32 " + (member ? "Host::" : "") + "M(List<int32>)");
        Assert.AreEqual(7, session.Run().Value);
    }

    /// <summary>
    /// A complete unsupported method trailer still rejects candidates that would leave the declaration invalid.
    /// </summary>
    /// <param name="trailer">The complete but unsupported implementation attribute.</param>
    [TestMethod]
    [DataRow("native")]
    [DataRow("unmanaged")]
    [DataRow("runtime")]
    public async Task Complete_InvalidMethodTrailer_ExcludesEarlierType(string trailer)
    {
        using var completer = new OperandCompleter(new Session());
        var reply = await Complete(completer, ".method void M(List<in|> value) cil " + trailer);
        Assert.DoesNotContain(item => item.InsertText == "int32", reply.Items);
    }

    /// <summary>
    /// Missing values, wrappers, and closing quotes do not suppress an earlier field type completion.
    /// </summary>
    /// <param name="text">The field declaration and marked caret.</param>
    /// <param name="insertion">The earlier field type.</param>
    [TestMethod]
    [DataRow(".field in| Value =", "int32")]
    [DataRow(".field in| Value = int32", "int32")]
    [DataRow(".field in| Value = int32(", "int32")]
    [DataRow(".field in| Value = int32(7", "int32")]
    [DataRow(".field in| Value = int32()", "int32")]
    [DataRow(".field in| Value", "int32")]
    [DataRow("/* before */ .field in| Value = /* kept */ int32(", "int32")]
    [DataRow(".field str| Value = \"unfinished", "string")]
    [DataRow(".field str| Value = \"unfinished\\\"", "string")]
    [DataRow(".field str| Value = bytearray(", "string")]
    public async Task Complete_UnfinishedInitializer_OffersEarlierType(string text, string insertion)
    {
        var session = new Session();
        session.AddLine(".class public Host {");
        using var completer = new OperandCompleter(session);
        var reply = await Complete(completer, text);
        Assert.Contains(item => item.InsertText == insertion, reply.Items);
    }

    /// <summary>
    /// Accepting the earlier field type and finishing its initializer preserves the literal metadata.
    /// </summary>
    /// <param name="unfinished">Whether the initializer is finished after accepting the field type.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_FieldInitializer_PreservesConstant(bool unfinished)
    {
        var session = new Session();
        session.AddLine(".class public Host {");
        using var completer = new OperandCompleter(session);
        var text = ".field public static literal in| Value = int32(" + (unfinished ? "" : "7)");
        var reply = await Complete(completer, text);
        session.AddLine(Accept(text, reply, "int32") + (unfinished ? "7)" : ""));
        session.AddLine("}");
        Assert.AreEqual(7, session.Types.Single().RuntimeType!.GetField("Value")!.GetRawConstantValue());
    }

    /// <summary>
    /// Complete incompatible initializers still exclude the field type that cannot hold their values.
    /// </summary>
    /// <param name="text">The invalid field declaration and marked caret.</param>
    [TestMethod]
    [DataRow(".field in| Value = \"text\"")]
    [DataRow(".field in| Value = \"text\\\\\"")]
    [DataRow(".field in| Value = int32(2147483648)")]
    public async Task Complete_InvalidInitializer_ExcludesEarlierType(string text)
    {
        var session = new Session();
        session.AddLine(".class public Host {");
        using var completer = new OperandCompleter(session);
        var reply = await Complete(completer, text);
        Assert.DoesNotContain(item => item.InsertText == "int32", reply.Items);
    }

    private Task<CompletionReply> Complete(OperandCompleter completer, string text)
    {
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        return completer.CompleteAsync(new CompletionRequest([text.Remove(caret, 1)], 0, caret, null, []),
            TestContext.CancellationToken);
    }

    private static string Accept(string text, CompletionReply reply, string insertion)
    {
        var item = reply.Items.Single(item => item.InsertText == insertion);
        var line = text.Replace("|", "", StringComparison.Ordinal);
        return line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];
    }
}
