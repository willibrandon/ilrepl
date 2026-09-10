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
    /// A jump offers only the overload with the enclosing method's complete signature.
    /// </summary>
    [TestMethod]
    public async Task Complete_JumpTarget_FiltersIncompatibleOverloads()
    {
        using var completer = new OperandCompleter(new Session());
        var prefix = "jmp Math::Abs";
        var reply = await completer.CompleteAsync(new CompletionRequest(
            [".method int32 Bridge(int32 value) {", prefix], 1, prefix.Length, null, []), TestContext.CancellationToken);
        Assert.AreSequenceEqual<string>(["Abs(int32)"], reply.Items.Select(item => item.Name).ToArray());
    }

    /// <summary>
    /// Return, argument-count and instance-convention mismatches stay available to ldftn but are absent from jmp.
    /// </summary>
    /// <param name="target">The incompatible target prefix.</param>
    /// <param name="name">The target's palette label.</param>
    [TestMethod]
    [DataRow("Console::WriteLine", "WriteLine(int32)")]
    [DataRow("Environment::get_TickCount", "get_TickCount()")]
    [DataRow("Random::Next", "Next(int32)")]
    public async Task Complete_JumpTarget_FiltersWithoutRestrictingFunctionPointers(string target, string name)
    {
        using var completer = new OperandCompleter(new Session());
        foreach (var opcode in new[] { "jmp", "ldftn" })
        {
            var prefix = opcode + " " + target;
            var reply = await completer.CompleteAsync(new CompletionRequest(
                [".method int32 Bridge(int32 value) {", prefix], 1, prefix.Length, null, []), TestContext.CancellationToken);
            Assert.AreEqual(opcode == "ldftn", reply.Items.Any(item => item.Name == name));
        }
    }

    /// <summary>
    /// Cell jumps use the generated object return and declared arguments instead of accepting arbitrary methods.
    /// </summary>
    [TestMethod]
    public async Task Complete_CellJump_UsesGeneratedSignatureAndRuns()
    {
        var session = new Session();
        foreach (var line in new[] { ".method object Identity(object value) {", "ldarg.0", "ret", "}" })
        {
            session.AddLine(line);
        }

        using var completer = new OperandCompleter(session);
        var prefix = "jmp Iden";
        var missingArgument = await completer.CompleteAsync(new CompletionRequest([prefix], 0, prefix.Length, null, []),
            TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.Name == "Identity(object)", missingArgument.Items);
        var declaration = ".args (object value = null)";
        var compatible = await completer.CompleteAsync(new CompletionRequest([declaration, prefix], 1, prefix.Length, null, []),
            TestContext.CancellationToken);
        var item = compatible.Items.Single(item => item.Name == "Identity(object)");
        session.AddLine(declaration);
        session.AddLine(prefix[..compatible.ReplaceStart] + item.InsertText);
        Assert.IsNull(session.Run().Value);

        var vararg = await completer.CompleteAsync(new CompletionRequest([".vararg", prefix], 1, prefix.Length, null, []),
            TestContext.CancellationToken);
        Assert.DoesNotContain(item => item.Name == "Identity(object)", vararg.Items);
    }

    /// <summary>
    /// Instance receivers and corresponding generic arguments survive accepted jumps to an own member.
    /// </summary>
    /// <param name="generic">Whether the source and target are generic static methods.</param>
    /// <param name="concrete">Whether the generic source has a concrete parameter type.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Complete_MemberJump_BindsAndRuns(bool generic, bool concrete)
    {
        var session = new Session();
        var member = generic ? "static !!0 Target<T>(!!0 value)" : "instance int32 Target(int32 value)";
        var source = concrete ? "static int32 Bridge<U>(int32 value)"
            : generic ? "static !!0 Bridge<U>(!!0 value)" : "instance int32 Bridge(int32 value)";
        var lines = new[] { ".class public JumpHost {", ".method public instance void .ctor() {",
            "ldarg.0", "call instance void Object::.ctor()", "ret", "}", ".method public " + member + " {",
            generic ? "ldarg.0" : "ldarg.1", "ret", "}", ".method public " + source + " {" };
        var prefix = concrete ? "jmp JumpHost::Target<int32>" : generic ? "jmp JumpHost::Target<!!0>" : "jmp JumpHost::Tar";
        using var completer = new OperandCompleter(session);
        if (generic)
        {
            var starter = "jmp JumpHost::Tar";
            var choices = await completer.CompleteAsync(new CompletionRequest([.. lines, starter], lines.Length, starter.Length, null, []),
                TestContext.CancellationToken);
            Assert.Contains(item => item.Continues && item.Name.Contains("Target", StringComparison.Ordinal), choices.Items);
        }

        var reply = await completer.CompleteAsync(new CompletionRequest([.. lines, prefix], lines.Length, prefix.Length, null, []),
            TestContext.CancellationToken);
        var item = reply.Items.Single(item => item.Name.StartsWith("Target", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        session.AddLine(prefix[..reply.ReplaceStart] + item.InsertText + prefix[(reply.ReplaceStart + reply.ReplaceLength)..]);
        session.AddLine("}");
        session.AddLine("}");
        if (!generic)
        {
            session.AddLine("newobj instance void JumpHost::.ctor()");
        }

        session.AddLine("ldc.i4.7");
        session.AddLine(generic ? "call int32 JumpHost::Bridge<int32>(int32)" : "callvirt instance int32 JumpHost::Bridge(int32)");
        Assert.AreEqual(7, session.Run().Value);
    }

    /// <summary>
    /// A hand-typed generic instantiation cannot offer a parameter list that differs from the enclosing signature.
    /// </summary>
    [TestMethod]
    public async Task Complete_GenericJump_RejectsIncompatibleInstantiation()
    {
        using var completer = new OperandCompleter(new Session());
        var lines = new[] { ".class public JumpHost {", ".method public static !!0 Target<T>(!!0 value) {",
            "ldarg.0", "ret", "}", ".method public static !!0 Bridge<U>(!!0 value) {", "jmp JumpHost::Target<string>" };
        var reply = await completer.CompleteAsync(new CompletionRequest(lines, lines.Length - 1, lines[^1].Length, null, []),
            TestContext.CancellationToken);
        Assert.IsEmpty(reply.Items);
    }

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
