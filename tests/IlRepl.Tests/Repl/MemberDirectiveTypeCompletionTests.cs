using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Type arguments in member directives pass the declaration checks once their reference is complete.
/// </summary>
[TestClass]
public sealed class MemberDirectiveTypeCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A completed constructor on a generic non-attribute type is excluded from an attribute declaration.
    /// </summary>
    /// <param name="text">The attribute declaration and marked caret.</param>
    [TestMethod]
    [DataRow(".custom instance void List<in|>::.ctor()")]
    [DataRow(".custom instance void List<List<in|>>::.ctor()")]
    [DataRow("/* before */ .custom instance void List<in|> /* kept */::.ctor()")]
    [DataRow(".custom instance void List<in|>::.ctor() = (01 00 00 00)")]
    public async Task Complete_NonAttributeConstructor_ExcludesArgument(string text)
    {
        var session = new Session();
        session.AddLine(".class public Host {");
        using var completer = new OperandCompleter(session);
        var reply = await Complete(completer, text);
        Assert.DoesNotContain(item => item.InsertText == "int32", reply.Items);
    }

    /// <summary>
    /// A valid generic attribute completion preserves its constructor and arguments in the emitted metadata.
    /// </summary>
    /// <param name="text">The attribute declaration and marked caret.</param>
    /// <param name="argument">The expected generic argument.</param>
    [TestMethod]
    [DataRow(".custom instance void Mark<in|>::.ctor()", typeof(int))]
    [DataRow(".custom instance void Mark<List<in|>>::.ctor()", typeof(List<int>))]
    [DataRow(".custom instance void Mark<in|>::.ctor() = (01 00 00 00)", typeof(int))]
    public async Task Complete_AttributeConstructor_PreservesMetadata(string text, Type argument)
    {
        var session = AttributeSession();
        using var completer = new OperandCompleter(session);
        var reply = await Complete(completer, text);
        var item = reply.Items.Single(item => item.InsertText == "int32");
        var line = text.Replace("|", "", StringComparison.Ordinal);
        session.AddLine(line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..]);
        session.AddLine("}");
        var host = session.Types.Single(type => type.RuntimeType!.Name == "Host").RuntimeType!;
        var attribute = host.GetCustomAttributesData().Single();
        Assert.AreEqual(argument, attribute.AttributeType.GetGenericArguments().Single());
        Assert.IsEmpty(attribute.ConstructorArguments);
    }

    /// <summary>
    /// Editing an argument does not require an unfinished attribute reference to include its constructor signature yet.
    /// </summary>
    /// <param name="text">The unfinished attribute declaration and marked caret.</param>
    [TestMethod]
    [DataRow(".custom instance void Mark<in|")]
    [DataRow(".custom instance void Mark<in|>")]
    [DataRow(".custom instance void Mark<in|>::")]
    [DataRow(".custom instance void Mark<in|>::.ctor")]
    [DataRow(".custom instance void Mark<in|>::.ctor(")]
    [DataRow(".custom instance void Mark<List<in|>>::.ctor(")]
    public async Task Complete_UnfinishedAttribute_OffersArgument(string text)
    {
        using var completer = new OperandCompleter(AttributeSession());
        var reply = await Complete(completer, text);
        Assert.Contains(item => item.InsertText == "int32", reply.Items);
    }

    /// <summary>
    /// An argument in a completed accessor obeys declaration rules, including duplicate accessor rejection.
    /// </summary>
    /// <param name="duplicate">Whether the property already has its getter.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_AccessorArgument_ConfirmsDeclaration(bool duplicate)
    {
        var session = new Session();
        foreach (var line in new[] { ".class public Host {", ".method public static List<int32> get_Items() {",
            "ldnull", "ret", "}", ".property List<int32> Items() {" })
        {
            session.AddLine(line);
        }

        if (duplicate)
        {
            session.AddLine(".get List<int32> get_Items()");
        }

        using var completer = new OperandCompleter(session);
        var reply = await Complete(completer, ".get List<in|> get_Items()");
        Assert.AreEqual(!duplicate, reply.Items.Any(item => item.InsertText == "int32"));
        if (!duplicate)
        {
            session.AddLine(".get List<int32> get_Items()");
            session.AddLine("}");
            session.AddLine("}");
            Assert.AreEqual(typeof(List<int>), session.Types.Single().RuntimeType!.GetProperty("Items")!.PropertyType);
        }
    }

    /// <summary>
    /// An override's declaring type arguments must match the implementing method's signature.
    /// </summary>
    /// <param name="valid">Whether the method returns the interface's completed argument.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_OverrideArgument_ConfirmsSignature(bool valid)
    {
        var session = new Session();
        foreach (var line in new[] { ".class public interface abstract IValue<T> {",
            ".method public abstract virtual instance !0 Get() { }", "}",
            ".class public Host implements IValue<int32> {",
            ".method public virtual instance " + (valid ? "int32" : "string") + " Read() {" })
        {
            session.AddLine(line);
        }

        using var completer = new OperandCompleter(session);
        var reply = await Complete(completer, ".override IValue<in|>::Get");
        Assert.AreEqual(valid, reply.Items.Any(item => item.InsertText == "int32"));
        if (valid)
        {
            session.AddLine(".override IValue<int32>::Get");
            session.AddLine("ldc.i4.7");
            session.AddLine("ret");
            session.AddLine("}");
            session.AddLine("}");
            var host = session.Types.Single(type => type.RuntimeType!.Name == "Host").RuntimeType!;
            Assert.AreEqual("Read", host.GetInterfaceMap(host.GetInterfaces().Single()).TargetMethods.Single().Name);
        }
    }

    private Task<CompletionReply> Complete(OperandCompleter completer, string text)
    {
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        return completer.CompleteAsync(new CompletionRequest([text.Remove(caret, 1)], 0, caret, null, []),
            TestContext.CancellationToken);
    }

    private static Session AttributeSession()
    {
        var session = new Session();
        foreach (var line in new[] { ".class public Mark<T> extends System.Attribute {", ".method public instance void .ctor() {",
            "ldarg.0", "call instance void System.Attribute::.ctor()", "ret", "}", "}", ".class public Host {" })
        {
            session.AddLine(line);
        }

        return session;
    }
}
