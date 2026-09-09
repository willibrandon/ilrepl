using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Jump operands complete accessible methods and execute with the enclosing method's signature.
/// </summary>
[TestClass]
public sealed class JumpCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Framework and bare session targets complete and receive the current method's arguments through jmp.
    /// </summary>
    /// <param name="sessionTarget">Whether the jump targets a session method.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_JumpTarget_BindsAndRuns(bool sessionTarget)
    {
        var session = new Session();
        if (sessionTarget)
        {
            foreach (var line in new[] { ".method int32 Next(int32 value) {", "ldarg.0", "ldc.i4.1", "add", "ret", "}" })
            {
                session.AddLine(line);
            }
        }

        using var completer = new OperandCompleter(session);
        var header = ".method int32 Bridge(int32 value) {";
        var prefix = sessionTarget ? "jmp Ne" : "jmp int32 Math::Ab";
        var reply = await completer.CompleteAsync(new CompletionRequest([header, prefix], 1, prefix.Length, null, []),
            TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.Name == (sessionTarget ? "Next(int32)" : "Abs(int32)"));
        var accepted = prefix[..reply.ReplaceStart] + item.InsertText + prefix[(reply.ReplaceStart + reply.ReplaceLength)..];
        session.AddLine(header);
        session.AddLine(accepted);
        session.AddLine("}");
        session.AddLine("ldc.i4.s -7");
        session.AddLine("call Bridge");
        Assert.AreEqual(sessionTarget ? -6 : 7, session.Run().Value);
    }
}
