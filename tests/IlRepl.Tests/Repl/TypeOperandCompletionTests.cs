using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Repl;

/// <summary>
/// Executable type operands respect their CLI shapes while type tokens and pointer storage remain available.
/// </summary>
[TestClass]
public sealed class TypeOperandCompletionTests
{
    /// <summary>
    /// Supplies cancellation for completion requests.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Every executable type opcode rejects void and managed-byref operands independently of the current stack.
    /// </summary>
    public static IEnumerable<object[]> InvalidOperands
    {
        get
        {
            foreach (var opcode in new[] { "box", "unbox", "unbox.any", "castclass", "isinst", "ldobj", "stobj", "cpobj",
                "initobj", "sizeof", "mkrefany", "refanyval", "constrained.", "ldelem", "ldelema", "stelem" })
            {
                yield return [opcode + " vo|", "void"];
                yield return [opcode + " int3|&", "int32"];
            }

            foreach (var opcode in new[] { "box", "unbox", "unbox.any", "castclass", "isinst", "constrained." })
            {
                yield return [opcode + " int3|*", "int32"];
            }

            yield return ["box typedre|", "typedref"];
            yield return ["box System.Spa|n<int32>", "Span<int32>"];
            yield return ["box int3|[]&", "int32"];
            yield return ["unbox str|", "string"];
            yield return ["unbox int3|[]", "int32"];
        }
    }

    /// <summary>
    /// The confirmed edit cannot introduce an invalid operand shape, including shapes formed by a retained suffix.
    /// </summary>
    /// <param name="text">The instruction with a marked caret.</param>
    /// <param name="excluded">The spelling that would make the operand invalid.</param>
    [TestMethod]
    [DynamicData(nameof(InvalidOperands))]
    public async Task Complete_InvalidShape_IsNotOffered(string text, string excluded)
    {
        using var completer = new OperandCompleter(new Session());
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.InsertText == excluded, reply.Items);
    }

    /// <summary>
    /// Type tokens preserve void, managed references, and unmanaged pointers as exact runtime identities.
    /// </summary>
    /// <param name="text">The partial token operand.</param>
    /// <param name="shape">The expected runtime shape.</param>
    [TestMethod]
    [DataRow("ldtoken vo|", 0)]
    [DataRow("ldtoken int3|&", 1)]
    [DataRow("ldtoken vo|*", 2)]
    public async Task Complete_Token_PreservesIdentity(string text, int shape)
    {
        var session = new Session();
        await AcceptAsync(session, text, shape == 1 ? "int32" : "void");
        session.AddLine("call Type::GetTypeFromHandle(RuntimeTypeHandle)");
        var expected = shape == 0 ? typeof(void) : shape == 1 ? typeof(int).MakeByRefType() : typeof(void).MakePointerType();
        Assert.AreEqual(expected, session.Run().Value);
    }

    /// <summary>
    /// Pointer and typed-reference storage can be measured, even when comments precede the retained pointer suffix.
    /// </summary>
    /// <param name="text">The partial sizeof operand.</param>
    /// <param name="chosen">The selected type name.</param>
    /// <param name="words">The number of native words in the type.</param>
    [TestMethod]
    [DataRow("sizeof vo|*", "void", 1)]
    [DataRow("sizeof vo| /* kept */*", "void", 1)]
    [DataRow("sizeof typedre|", "typedref", 2)]
    public async Task Complete_StoragePointer_Runs(string text, string chosen, int words)
    {
        var session = new Session();
        await AcceptAsync(session, text, chosen);
        Assert.AreEqual((uint)(IntPtr.Size * words), session.Run().Value);
    }

    /// <summary>
    /// Boxing references is an identity operation, and casts to boxed value types remain valid.
    /// </summary>
    /// <param name="opcode">The completed operation.</param>
    [TestMethod]
    [DataRow("box")]
    [DataRow("unbox.any")]
    [DataRow("castclass")]
    [DataRow("isinst")]
    public async Task Complete_BoxableValues_Runs(string opcode)
    {
        var session = new Session();
        if (opcode is "box" or "unbox.any")
        {
            session.AddLine("ldstr \"kept\"");
            await AcceptAsync(session, opcode + " str|", "string");
            Assert.AreEqual("kept", session.Run().Value);
        }
        else
        {
            session.AddLine("ldc.i4.7");
            session.AddLine("box int32");
            await AcceptAsync(session, opcode + " int3|", "int32");
            session.AddLine("unbox.any int32");
            Assert.AreEqual(7, session.Run().Value);
        }
    }

    private async Task AcceptAsync(Session session, string text, string chosen)
    {
        using var completer = new OperandCompleter(session);
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var line = text.Remove(caret, 1);
        var reply = await completer.CompleteAsync(new CompletionRequest([line], 0, caret, null, []), TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.InsertText == chosen);
        session.AddLine(line[..reply.ReplaceStart] + item.InsertText + line[(reply.ReplaceStart + reply.ReplaceLength)..]);
    }
}
