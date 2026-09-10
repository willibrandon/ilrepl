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
    /// An unfinished later inheritance clause leaves valid earlier type components available for completion.
    /// </summary>
    /// <param name="text">The incomplete declaration and marked caret.</param>
    /// <param name="insertion">The valid earlier type component.</param>
    [TestMethod]
    [DataRow(".class D extends List<in|> implements", "int32")]
    [DataRow(".class D extends List<in|> implements ", "int32")]
    [DataRow(".class D extends List<in|> implements class", "int32")]
    [DataRow(".class D extends List<in|> implements [System.Runtime]", "int32")]
    [DataRow(".class D extends List<in|> implements class [System.Runtime]", "int32")]
    [DataRow(".class D extends List<in|> implements IEnumerable<int32>,", "int32")]
    [DataRow(".class D extends List<in|> implements IEnumerable<", "int32")]
    [DataRow(".class D extends List<in|> implements IEnumerable<int32", "int32")]
    [DataRow(".class D extends List<in|> implements IEnumerable<>", "int32")]
    [DataRow(".class D extends List<in|> implements IEnumerable<class>", "int32")]
    [DataRow(".class D extends List<in|> implements IEnumerable<[System.Runtime]>", "int32")]
    [DataRow(".class D extends List<in|> implements IDictionary<int32,>", "int32")]
    [DataRow(".class D extends List<in|> implements IEnumerable<List<>>", "int32")]
    [DataRow(".class D extends List<in|> implements IEnumerable<int32>, class", "int32")]
    [DataRow(".class D implements IEnumerable<in|>,", "int32")]
    [DataRow(".class D implements IEnumerable<in|>, IEnumerable<", "int32")]
    [DataRow(".class D implements IEnumerable<in|>, class [System.Runtime]", "int32")]
    [DataRow(".class D<(IEnumerable<in|>) T> extends", "int32")]
    [DataRow(".class D<(IEnumerable<in|>) T> extends class", "int32")]
    [DataRow(".class D<(IEnumerable<in|>) T> extends List<int32> implements", "int32")]
    [DataRow(".class D extends Obj|ect implements", "object")]
    [DataRow("/* before */ .class D extends List<in|> implements /* kept */", "int32")]
    public async Task Complete_UnfinishedInheritance_OffersEarlierComponent(string text, string insertion)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var reply = await completer.CompleteAsync(new CompletionRequest([text.Remove(caret, 1)], 0, caret, null, []),
            TestContext.CancellationToken);
        Assert.Contains(item => item.InsertText == insertion, reply.Items);
    }

    /// <summary>
    /// Finishing the later clause restores full declaration validation for an otherwise valid earlier argument.
    /// </summary>
    /// <param name="text">The complete declaration and marked caret.</param>
    [TestMethod]
    [DataRow(".class D extends List<in|> implements int32")]
    [DataRow(".class D extends List<in|> implements IEnumerable<int32>, string")]
    [DataRow(".class D<(IEnumerable<in|>) T> extends string")]
    [DataRow(".class D implements IEnumerable<in|>, string")]
    public async Task Complete_InvalidLaterInheritance_ExcludesEarlierComponent(string text)
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
    /// <param name="unfinished">Whether the interface clause is finished after accepting the earlier argument.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_GenericBase_BindsAndRuns(bool unfinished)
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var text = ".class public Derived extends List<in|>" + (unfinished ? " implements" : " {");
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == "int32");
        session.AddLine(line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..]
            + (unfinished ? " IEnumerable<int32> {" : ""));
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
