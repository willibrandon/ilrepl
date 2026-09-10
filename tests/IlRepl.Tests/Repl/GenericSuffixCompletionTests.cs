using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Generic argument completion checks constraints after retaining compound type suffixes.
/// </summary>
[TestClass]
public sealed class GenericSuffixCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Retained arrays, pointers, and references participate in type and method argument constraints.
    /// </summary>
    /// <param name="constraint">The owner's generic parameter constraint.</param>
    /// <param name="suffix">The text retained after the edited type name.</param>
    /// <param name="allowed">Whether int32 with this suffix satisfies the constraint.</param>
    /// <param name="method">Whether the generic owner is a method.</param>
    [TestMethod]
    [DataRow("class", "[]", true, false)]
    [DataRow("class", "[][]", true, false)]
    [DataRow("class", "[0...,0...]", true, false)]
    [DataRow("class", " /* retained */ []", true, false)]
    [DataRow("class", "", false, false)]
    [DataRow("valuetype", "[]", false, false)]
    [DataRow("valuetype", "", true, false)]
    [DataRow(".ctor", "[]", false, false)]
    [DataRow("", "*", false, false)]
    [DataRow("", "&", false, false)]
    [DataRow("class", "[]", true, true)]
    [DataRow("valuetype", "[]", false, true)]
    public async Task Complete_CompoundArgument_ChecksCompletedType(string constraint, string suffix, bool allowed, bool method)
    {
        var session = new Session();
        if (method)
        {
            session.AddLine(".class public SuffixHost {");
            session.AddLine($".method public static int32 Value<{constraint} T>() {{");
            session.AddLine("ldc.i4.7");
            session.AddLine("ret");
            session.AddLine("}");
            session.AddLine("}");
        }
        else
        {
            session.AddLine($".class public SuffixHost<{constraint} T> {{ }}");
        }

        using var completer = new OperandCompleter(session);
        var prefix = method ? "call SuffixHost::Value<in" : "ldtoken SuffixHost<in";
        var line = prefix + suffix + ">";
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, prefix.Length, null, []),
            TestContext.CancellationToken);
        var item = reply.Items.SingleOrDefault(item => item.InsertText == "int32");
        if (!allowed)
        {
            Assert.IsNull(item);
            return;
        }

        Assert.IsNotNull(item);
        var completed = line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..];
        session.AddLine(completed + (method ? "()" : ""));
        if (!method)
        {
            session.AddLine("pop");
            session.AddLine("ldc.i4.7");
        }

        Assert.AreEqual(7, session.Run().Value);
    }
}
