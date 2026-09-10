using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Closed generic operands validate their outer constructions before publishing an inner argument completion.
/// </summary>
[TestClass]
public sealed class GenericOperandCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An inner argument cannot bypass an outer constraint or leave an invalid executable operand shape.
    /// </summary>
    /// <param name="text">The instruction and marked caret.</param>
    [TestMethod]
    [DataRow("ldtoken Nullable<List<in|>>")]
    [DataRow("ldtoken Nullable<List<in|>>[]")]
    [DataRow("/* before */ L: ldtoken Nullable<List<in|>> /* kept */[]")]
    [DataRow("ldtoken Dictionary<string, Nullable<List<in|>>>")]
    [DataRow("call Array::Empty<Nullable<List<in|>>>()")]
    [DataRow("call Array::Empty<Nullable<List<in|>>>")]
    [DataRow("call Nullable<List<in|>>::get_HasValue()")]
    [DataRow("call Nullable<List<in|>>::")]
    [DataRow("call void Console::WriteLine(Nullable<List<in|>>)")]
    [DataRow("box List<in|>&")]
    [DataRow("unbox List<in|>")]
    [DataRow("newarr List<in|>&")]
    [DataRow("callvirt Array::Empty<List<in|>>()")]
    [DataRow(".dis Array::Empty<Nullable<List<in|>>>()")]
    [DataRow(".typeargs (Nullable<List<in|>>)")]
    public async Task Complete_InvalidOuterOperand_IsNotOffered(string text)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var reply = await completer.CompleteAsync(new CompletionRequest([text.Remove(caret, 1)], 0, caret, null, []),
            TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.InsertText == "int32", reply.Items);
    }

    /// <summary>
    /// A valid argument remains available when the outer type or the method's parameter list is unfinished.
    /// </summary>
    /// <param name="text">The incomplete instruction and marked caret.</param>
    [TestMethod]
    [DataRow("ldtoken List<Nullable<in|")]
    [DataRow("ldtoken List<Nullable<in|>")]
    [DataRow("ldtoken Dictionary<in|,>")]
    [DataRow("call Array::Empty<List<in|")]
    [DataRow("call Array::Empty<List<in|>>")]
    [DataRow("call List<in|>::")]
    public async Task Complete_UnfinishedOperand_OffersArgument(string text)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var reply = await completer.CompleteAsync(new CompletionRequest([text.Remove(caret, 1)], 0, caret, null, []),
            TestContext.CancellationToken);
        Assert.Contains(item => item.InsertText == "int32", reply.Items);
    }

    /// <summary>
    /// Completing a closed nested construction produces a token with the exact expected runtime identity.
    /// </summary>
    /// <param name="text">The valid instruction and marked caret.</param>
    /// <param name="expected">The expected runtime type.</param>
    [TestMethod]
    [DataRow("ldtoken List<Nullable<in|>>", typeof(List<int?>))]
    [DataRow("ldtoken Nullable<KeyValuePair<in|, string>>", typeof(KeyValuePair<int, string>?))]
    [DataRow("ldtoken List<in|>&", typeof(List<int>))]
    public async Task Complete_ValidOuterOperand_BindsAndRuns(string text, Type expected)
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == "int32");
        session.AddLine(line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..]);
        session.AddLine("call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        Assert.AreEqual(line.EndsWith('&') ? expected.MakeByRefType() : expected, session.Run().Value);
    }

    /// <summary>
    /// Completing an argument inside a constructed method's argument preserves an executable call.
    /// </summary>
    [TestMethod]
    public async Task Complete_NestedMethodArgument_BindsAndRuns()
    {
        var session = new Session();
        using var completer = new OperandCompleter(session);
        const string text = "call Array::Empty<List<in|>>()";
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == "int32");
        session.AddLine(line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..]);
        session.AddLine("ldlen");
        session.AddLine("conv.i4");
        Assert.AreEqual(0, session.Run().Value);
    }

    /// <summary>
    /// A closed method argument validates its outer method constraints even before a parameter list is supplied.
    /// </summary>
    /// <param name="owner">The operation whose generic argument is edited.</param>
    /// <param name="parameters">The explicit parameter list, or empty for signature continuation.</param>
    [TestMethod]
    [DataRow("call", "")]
    [DataRow("call", "()")]
    [DataRow(".dis", "")]
    [DataRow(".dis", "()")]
    public async Task Complete_OuterMethodConstraint_ValidatesBeforeSignature(string owner, string parameters)
    {
        var session = new Session();
        foreach (var line in new[] { ".class public Host {", ".method public static int32 Value<valuetype T>() {",
            "ldc.i4.7", "ret", "}", "}" })
        {
            session.AddLine(line);
        }

        using var completer = new OperandCompleter(session);
        foreach (var valid in new[] { false, true })
        {
            var argument = valid ? "KeyValuePair<in|, string>" : "List<in|>";
            var text = owner + " Host::Value<" + argument + ">" + parameters;
            var caret = text.IndexOf('|', StringComparison.Ordinal);
            var reply = await completer.CompleteAsync(new CompletionRequest([text.Remove(caret, 1)], 0, caret, null, []),
                TestContext.CancellationToken);
            Assert.AreEqual(valid, reply.Items.Any(item => item.InsertText == "int32"), text);
        }
    }
}
