using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Keeps contextual help consistent with validated transfers and the enclosing generic operand.
/// </summary>
[TestClass]
public sealed class ContextualInstructionRegressionTests
{
    /// <summary>
    /// Supplies cancellation for analysis and completion through both engine transports.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejected arithmetic retains generic help and explicitly refuses to claim a successful typed transfer.
    /// </summary>
    /// <param name="opcode">The rejected unary or binary instruction.</param>
    /// <param name="useHost">Whether to use the real host process.</param>
    [TestMethod]
    [DataRow("neg", false)]
    [DataRow("neg", true)]
    [DataRow("add", false)]
    [DataRow("add", true)]
    public async Task InvalidArithmetic_ReportsUnavailableTransfer(string opcode, bool useHost)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = useHost ? (IReplEngine)await HostPaths.StartEngineAsync(ct) : new InProcessEngine();
        string[] lines = opcode == "neg" ? ["ldstr \"wrong\"", opcode] : ["ldstr \"wrong\"", "ldc.i4.1", opcode];
        var reply = await engine.AnalyzeAsync(new(lines, lines.Length - 1, opcode.Length, 1), ct);
        var diagnostic = Assert.ContainsSingle(reply.Diagnostics.Where(item => item.Code == "FLOW005"));
        Assert.IsNotNull(reply.InstructionHelp);
        Assert.AreEqual(InstructionReference.For(opcode).StackEffect, reply.InstructionHelp.StackEffect);
        Assert.Contains(note => note.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || note.Contains("unavailable", StringComparison.OrdinalIgnoreCase), reply.InstructionHelp.Notes);
        Assert.IsNotNull(diagnostic.Explanation?.Instruction);
        Assert.AreEqual(InstructionReference.For(opcode).StackEffect, diagnostic.Explanation.Instruction.StackEffect);
        Assert.Contains(note => note.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || note.Contains("unavailable", StringComparison.OrdinalIgnoreCase), diagnostic.Explanation.Instruction.Notes);
    }

    /// <summary>
    /// A validated arithmetic transfer still shows its established concrete operand and result types.
    /// </summary>
    [TestMethod]
    public async Task ValidArithmetic_PreservesEstablishedTransfer()
    {
        await using var engine = new InProcessEngine();
        var reply = await engine.AnalyzeAsync(new(["ldc.i4.1", "neg"], 1, 3, 1), TestContext.CancellationToken);
        Assert.IsEmpty(reply.Diagnostics);
        Assert.IsNotNull(reply.InstructionHelp);
        Assert.AreEqual("[int32] → [int32]", reply.InstructionHelp.StackEffect);
    }

    /// <summary>
    /// Completing an inner type argument describes the resolved enclosing instruction rather than the selected type.
    /// </summary>
    /// <param name="text">The instruction containing the marked type-argument caret.</param>
    /// <param name="member">The enclosing member that must remain in the instruction syntax.</param>
    /// <param name="useHost">Whether to use the real host process.</param>
    [TestMethod]
    [DataRow("call Enumerable::Empty<int3|>()", "::Empty<int32>()", false)]
    [DataRow("call Enumerable::Empty<int3|>()", "::Empty<int32>()", true)]
    [DataRow("newobj List<int3|>::.ctor()", "::.ctor()", false)]
    [DataRow("newobj List<int3|>::.ctor()", "::.ctor()", true)]
    [DataRow("ldtoken method Enumerable::Empty<int3|>()", "::Empty<int32>()", false)]
    public async Task GenericArgument_HelpNamesEnclosingMember(string text, string member, bool useHost)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = useHost ? (IReplEngine)await HostPaths.StartEngineAsync(ct) : new InProcessEngine();
        var caret = text.IndexOf('|', StringComparison.Ordinal);
        var reply = await engine.CompleteAsync(new([text.Remove(caret, 1)], 0, caret, null, []), ct);
        var item = reply.Items.Single(candidate => candidate.InsertText == "int32");
        Assert.IsNotNull(item.InstructionHelp);
        Assert.Contains(member, item.InstructionHelp.Syntax);
        Assert.Contains("int32", item.InstructionHelp.Syntax);
        Assert.DoesNotContain("\n", item.InstructionHelp.Syntax);
        Assert.DoesNotContain("type argument", item.InstructionHelp.Syntax);
        Assert.IsNotNull(item.FullDetail);
        Assert.StartsWith("int32\n", item.FullDetail);
        Assert.Contains("type argument", item.FullDetail);
        var bound = await engine.AnalyzeAsync(new([item.InstructionHelp.Syntax], 0, 0, 1), ct);
        Assert.IsEmpty(bound.Diagnostics, string.Join("; ", bound.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    /// <summary>
    /// Static field signatures remain valid instruction operands and field tokens retain their required operand marker.
    /// </summary>
    /// <param name="text">The partial static field operand.</param>
    /// <param name="syntax">The complete instruction syntax expected from the selected field.</param>
    [TestMethod]
    [DataRow("ldsfld string::Emp", "ldsfld string string::Empty")]
    [DataRow("ldtoken field string::Emp", "ldtoken field string string::Empty")]
    public async Task StaticField_HelpUsesBindableInstructionSyntax(string text, string syntax)
    {
        await using var engine = new InProcessEngine();
        var ct = TestContext.CancellationToken;
        var reply = await engine.CompleteAsync(new([text], 0, text.Length, null, []), ct);
        var item = Assert.ContainsSingle(reply.Items);
        Assert.IsNotNull(item.InstructionHelp);
        Assert.AreEqual(syntax, item.InstructionHelp.Syntax);
        Assert.StartsWith("static string string::Empty", item.FullDetail!);
        var bound = await engine.AnalyzeAsync(new([syntax], 0, 0, 1), ct);
        Assert.IsEmpty(bound.Diagnostics, string.Join("; ", bound.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    /// <summary>
    /// Generic starters retain the selected definition while documenting that argument-dependent facts remain unresolved.
    /// </summary>
    /// <param name="text">The partial generic method or constructor owner.</param>
    /// <param name="insertion">The selected generic starter.</param>
    /// <param name="useHost">Whether to use the real host process.</param>
    [TestMethod]
    [DataRow("call Enumerable::Emp", "Enumerable::Empty<", false)]
    [DataRow("call Enumerable::Emp", "Enumerable::Empty<", true)]
    [DataRow("newobj List", "List<", false)]
    [DataRow("newobj List", "List<", true)]
    [DataRow("ldtoken method Enumerable::Emp", "Enumerable::Empty<", false)]
    public async Task GenericStarter_ExplainsSelectedDefinition(string text, string insertion, bool useHost)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = useHost ? (IReplEngine)await HostPaths.StartEngineAsync(ct) : new InProcessEngine();
        var reply = await engine.CompleteAsync(new([text], 0, text.Length, null, []), ct);
        var item = reply.Items.Single(candidate => candidate.InsertText == insertion);
        Assert.IsTrue(item.Continues);
        Assert.IsNotNull(item.Continuation);
        Assert.IsNotNull(item.InstructionHelp);
        Assert.IsNotNull(item.FullDetail);
        var opcode = text[..text.IndexOf(' ', StringComparison.Ordinal)];
        Assert.AreEqual(opcode, item.InstructionHelp.Mnemonic);
        Assert.StartsWith(opcode + " ", item.InstructionHelp.Syntax);
        var signature = item.FullDetail.Split('\n')[0];
        Assert.Contains(signature.StartsWith("static ", StringComparison.Ordinal) ? signature[7..] : signature,
            item.InstructionHelp.Syntax);
        Assert.DoesNotContain(opcode + " static ", item.InstructionHelp.Syntax);
        if (text.StartsWith("ldtoken method ", StringComparison.Ordinal))
        {
            Assert.StartsWith("ldtoken method ", item.InstructionHelp.Syntax);
        }
        Assert.AreEqual(InstructionReference.For(opcode).Explanation, item.InstructionHelp.Explanation);
        Assert.AreEqual(InstructionReference.For(opcode).DocumentationUrl, item.InstructionHelp.DocumentationUrl);
        Assert.Contains(note => note.Contains("type arguments", StringComparison.OrdinalIgnoreCase)
            && note.Contains("unresolved", StringComparison.OrdinalIgnoreCase), item.InstructionHelp.Notes);
    }

    /// <summary>
    /// Starting a nested generic argument keeps the unfinished enclosing member visible in contextual help.
    /// </summary>
    [TestMethod]
    public async Task NestedGenericStarter_PreservesEnclosingOperand()
    {
        await using var engine = new InProcessEngine();
        const string text = "call Enumerable::Empty<Lis";
        var reply = await engine.CompleteAsync(new([text], 0, text.Length, null, []), TestContext.CancellationToken);
        var item = reply.Items.Single(candidate => candidate.InsertText == "List<");
        Assert.IsNotNull(item.InstructionHelp);
        Assert.AreEqual("call Enumerable::Empty<List<", item.InstructionHelp.Syntax);
        Assert.AreEqual(InstructionReference.For("call").DocumentationUrl, item.InstructionHelp.DocumentationUrl);
        Assert.Contains(note => note.Contains("unresolved", StringComparison.OrdinalIgnoreCase), item.InstructionHelp.Notes);
    }
}
