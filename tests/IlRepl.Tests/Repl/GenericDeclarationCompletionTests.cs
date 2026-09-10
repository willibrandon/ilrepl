using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Completing a generic argument preserves the enclosing declaration's type rules.
/// </summary>
[TestClass]
public sealed class GenericDeclarationCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Completed base types and interfaces are checked after replacing a nested generic argument.
    /// </summary>
    /// <param name="text">The declaration and marked caret.</param>
    [TestMethod]
    [DataRow(".class public Derived extends List<in|>[] {")]
    [DataRow(".class public Derived extends List<in|>& {")]
    [DataRow(".class public Derived extends IEnumerable<in|> {")]
    [DataRow(".class public Derived implements IEnumerable<in|>[] {")]
    [DataRow(".class public Derived implements List<in|> {")]
    public async Task Complete_InvalidBase_IsNotOffered(string text)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var reply = await completer.CompleteAsync(new CompletionRequest([text.Remove(caret, 1)], 0, caret, null, []),
            TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.InsertText == "int32", reply.Items);
    }

    /// <summary>
    /// A valid generic base completion creates the declared inheritance relationship and executes its inherited members.
    /// </summary>
    [TestMethod]
    public async Task Complete_GenericBase_BindsAndRuns()
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var text = ".class public Derived extends List<in|> {";
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == "int32");
        session.AddLine(line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..]);
        session.AddLine(".method public instance void .ctor() {");
        session.AddLine("ldarg.0");
        session.AddLine("call instance void List<int32>::.ctor()");
        session.AddLine("ret");
        session.AddLine("}");
        session.AddLine("}");
        Assert.AreEqual(typeof(List<int>), session.Types.Single().RuntimeType!.BaseType);
        session.AddLine("newobj Derived::.ctor()");
        session.AddLine("callvirt List<int32>::get_Count()");
        Assert.AreEqual(0, session.Run().Value);
    }
}
