using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Incomplete fields and properties offer usable value types while retaining pointer fields.
/// </summary>
[TestClass]
public sealed class FieldTypeCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Field and property type checks apply before a name completes the header.
    /// </summary>
    /// <param name="text">The unfinished declaration and caret.</param>
    /// <param name="excluded">The type that cannot be used here.</param>
    [TestMethod]
    [DataRow(".field public vo|", "void")]
    [DataRow(".field public System.Voi|", "void")]
    [DataRow(".field public vo|[]", "void")]
    [DataRow(".field public vo|&", "void")]
    [DataRow(".field public vo| modopt(System.Runtime.CompilerServices.IsReadOnlyAttribute)", "void")]
    [DataRow(".field public int3| pinned", "int32")]
    [DataRow(".property vo|", "void")]
    [DataRow(".property int32 Item(vo|", "void")]
    public async Task Complete_UnfinishedValue_ExcludesInvalidType(string text, string excluded)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([".class public FieldHost {", line], 1, caret, null, []),
            TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.InsertText == excluded, reply.Items);
    }

    /// <summary>
    /// Void pointer fields and function pointer returns remain available before the field name is written.
    /// </summary>
    /// <param name="text">The partial field declaration and caret.</param>
    [TestMethod]
    [DataRow(".field public static vo|*")]
    [DataRow(".field public static vo| /* retained */ *")]
    [DataRow("/* before */ .field public static vo| /* retained */ *")]
    [DataRow(".field public static method vo| *()")]
    public async Task Complete_PointerField_BindsAndRuns(string text)
    {
        var session = new Session();
        session.AddLine(".class public FieldHost {");
        using var completer = new OperandCompleter(session);
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == "void");
        session.AddLine(line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..] + " Address");
        session.AddLine("}");
        session.AddLine("ldsfld FieldHost::Address");
        session.AddLine("pop");
        session.AddLine("ldc.i4.7");
        Assert.AreEqual(7, session.Run().Value);
    }
}
