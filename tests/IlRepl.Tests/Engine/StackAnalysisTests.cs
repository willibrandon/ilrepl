using IlRepl.Engine;
using IlRepl.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodDefinition = Mono.Cecil.MethodDefinition;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="StackAnalysis"/>: joins, loops, dead code, handler seeds, and the three column states.
/// </summary>
[TestClass]
public sealed class StackAnalysisTests
{
    private static DisassembledMethod Body(Action<ModuleDefinition, TypeDefinition, ILProcessor, MethodDefinition> emit, Mono.Cecil.TypeReference? returnType = null)
    {
        var session = new Session();
        var (_, _, fixture) = CecilFixture.Build((module, type) =>
        {
            var m = new MethodDefinition("M", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, returnType ?? module.TypeSystem.Void);
            type.Methods.Add(m);
            emit(module, type, m.Body.GetILProcessor(), m);
        }, session.Resolver);
        return MethodDisassembler.Disassemble(fixture.GetMethod("M")!, session);
    }

    /// <summary>
    /// A join merges by category: a byte argument and an int32 constant meet as int32, and object meets string as object.
    /// </summary>
    [TestMethod]
    public void Run_Diamond_MergesByCategoryAndCommonBase()
    {
        var method = Body((module, _, il, m) =>
        {
            m.Parameters.Add(new ParameterDefinition("flag", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Boolean));
            m.Parameters.Add(new ParameterDefinition("b", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Byte));
            var other = il.Create(OpCodes.Ldarg_1);
            var join = il.Create(OpCodes.Pop);
            var refOther = il.Create(OpCodes.Ldstr, "s");
            var refJoin = il.Create(OpCodes.Pop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, other);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Br, join);
            il.Append(other);
            il.Append(join);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, refOther);
            il.Emit(OpCodes.Newobj, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            il.Emit(OpCodes.Br, refJoin);
            il.Append(refOther);
            il.Append(refJoin);
            il.Emit(OpCodes.Ret);
        });
        var lines = DisassemblyText.LinesWithStack(method);
        var text = string.Join("\n", lines);
        Assert.Contains("ldc.i4.1\t[int32]", text);
        Assert.Contains("ldarg.1\t[uint8]", text);
        var join = method.Clauses.Count == 0 ? method.Entries.First(e => e.Instruction?.Op.Name == "pop") : throw new AssertFailedException("no clauses expected");
        Assert.AreEqual("[]", DisassemblyText.StackAt(method, join.Offset));
        // The merged state at the join is what the pop consumed: int32 on both paths.
        var pops = method.Entries.Where(e => e.Instruction?.Op.Name == "pop").ToList();
        Assert.HasCount(2, pops);
        Assert.Contains("ldstr \"s\"\t[string]", text);
        Assert.Contains("newobj instance void object::.ctor()\t[object]", text);
    }

    /// <summary>
    /// The merged type at a join is visible when the join line itself pushes nothing: a dup shows it.
    /// </summary>
    [TestMethod]
    public void Run_Join_ShowsMergedType()
    {
        var method = Body((module, _, il, m) =>
        {
            m.Parameters.Add(new ParameterDefinition("flag", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Boolean));
            var other = il.Create(OpCodes.Ldstr, "s");
            var join = il.Create(OpCodes.Dup);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, other);
            il.Emit(OpCodes.Newobj, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!));
            il.Emit(OpCodes.Br, join);
            il.Append(other);
            il.Append(join);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });
        var dup = method.Entries.First(e => e.Instruction?.Op.Name == "dup");
        Assert.AreEqual("[object, object]", DisassemblyText.StackAt(method, dup.Offset));
    }

    /// <summary>
    /// A loop converges with the right depth, and code after an unconditional branch is unreachable.
    /// </summary>
    [TestMethod]
    public void Run_LoopAndDeadCode_ConvergeAndMarkUnreachable()
    {
        var method = Body((module, _, il, m) =>
        {
            m.Parameters.Add(new ParameterDefinition("n", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            var top = il.Create(OpCodes.Ldarg_0);
            var exit = il.Create(OpCodes.Ret);
            il.Append(top);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Starg_S, m.Parameters[0]);
            il.Emit(OpCodes.Brtrue, top);
            il.Emit(OpCodes.Br, exit);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Pop);
            il.Append(exit);
        });
        var lines = DisassemblyText.LinesWithStack(method);
        var text = string.Join("\n", lines);
        Assert.Contains("brtrue IL_0000\t[]", text);
        Assert.Contains("ldnull\tunreachable", text);
        Assert.Contains("pop\tunreachable", text);
        Assert.Contains("ret\t[]", text);
        Assert.DoesNotContain("?", text);
    }

    /// <summary>
    /// A proven underflow remains invalid while independent handler entry stacks can still be inspected.
    /// </summary>
    [TestMethod]
    public void Run_Underflow_IsInvalidWhileHandlersRemainAnalyzable()
    {
        var method = Body((module, _, il, m) =>
        {
            var tryStart = il.Create(OpCodes.Pop);
            var handler = il.Create(OpCodes.Pop);
            var end = il.Create(OpCodes.Ret);
            il.Append(tryStart);
            il.Emit(OpCodes.Nop);
            il.Emit(OpCodes.Leave, end);
            il.Append(handler);
            il.Emit(OpCodes.Leave, end);
            il.Append(end);
            m.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = end, CatchType = module.ImportReference(typeof(Exception)) });
        });
        var lines = DisassemblyText.LinesWithStack(method);
        var text = string.Join("\n", lines);
        Assert.Contains("0000 pop\tinvalid", text);
        Assert.Contains("nop\tinvalid", text);
        Assert.Contains("leave IL_", text);
        var handler = method.Clauses[0].HandlerStart;
        Assert.AreEqual("[]", DisassemblyText.StackAt(method, handler));

        // Clearing the stack does not erase a proven error on the path that reaches the exit.
        Assert.EndsWith("ret\tinvalid", lines[^1]);
    }

    /// <summary>
    /// A known predecessor never erases a proven error on another incoming path.
    /// </summary>
    [TestMethod]
    public void Run_InvalidPath_IsNotErasedAtAJoin()
    {
        var method = Body((module, _, il, m) =>
        {
            m.Parameters.Add(new ParameterDefinition("flag", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Boolean));
            var bad = il.Create(OpCodes.Pop);
            var join = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, bad);
            il.Emit(OpCodes.Br, join);
            il.Append(bad);
            il.Emit(OpCodes.Br, join);
            il.Append(join);
            il.Emit(OpCodes.Ret);
        });
        var join = method.Entries.First(e => e.Instruction?.Op.Name == "nop");
        Assert.AreEqual("invalid", DisassemblyText.StackAt(method, join.Offset));
    }

    /// <summary>
    /// Depths that disagree at a join produce a correctness diagnostic.
    /// </summary>
    [TestMethod]
    public void Run_DepthMismatch_IsInvalid()
    {
        var method = Body((module, _, il, m) =>
        {
            m.Parameters.Add(new ParameterDefinition("flag", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Boolean));
            var deep = il.Create(OpCodes.Ldc_I4_1);
            var join = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, deep);
            il.Emit(OpCodes.Br, join);
            il.Append(deep);
            il.Emit(OpCodes.Br, join);
            il.Append(join);
            il.Emit(OpCodes.Ret);
        });
        var join = method.Entries.First(e => e.Instruction?.Op.Name == "nop");
        Assert.AreEqual("invalid", DisassemblyText.StackAt(method, join.Offset));
    }

    /// <summary>
    /// A null merges with a reference as that reference, and a boxed value with a reference as object.
    /// </summary>
    [TestMethod]
    public void Run_NullAndBoxedJoins()
    {
        var method = Body((module, _, il, m) =>
        {
            m.Parameters.Add(new ParameterDefinition("flag", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Boolean));
            var other = il.Create(OpCodes.Ldnull);
            var join = il.Create(OpCodes.Dup);
            var other2 = il.Create(OpCodes.Ldstr, "s");
            var join2 = il.Create(OpCodes.Dup);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, other);
            il.Emit(OpCodes.Ldstr, "t");
            il.Emit(OpCodes.Br, join);
            il.Append(other);
            il.Append(join);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, other2);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Box, module.TypeSystem.Int32);
            il.Emit(OpCodes.Br, join2);
            il.Append(other2);
            il.Append(join2);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });
        var dups = method.Entries.Where(e => e.Instruction?.Op.Name == "dup").ToList();
        Assert.AreEqual("[string, string]", DisassemblyText.StackAt(method, dups[0].Offset));
        Assert.AreEqual("[object, object]", DisassemblyText.StackAt(method, dups[1].Offset));
    }

    /// <summary>
    /// A decoded method is complete, so a missing destination is invalid rather than a pending source edit.
    /// </summary>
    [TestMethod]
    public void MissingDecodedTarget_IsAnError()
    {
        var method = Body((_, _, il, _) =>
        {
            var target = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Br, target);
            il.Append(target);
        });
        method = method with
        {
            Entries = method.Entries.Select(entry => entry.Raw?.BranchTarget is not null
                ? entry with { Raw = entry.Raw with { BranchTarget = 12345 } } : entry).ToArray(),
        };
        var column = StackAnalysis.Run(method, out var diagnostics);
        Assert.IsNotEmpty(column);
        Assert.Contains(diagnostic => diagnostic.Code == "FLOW002" && diagnostic.Kind == AnalysisDiagnosticKind.Error, diagnostics);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Incomplete, diagnostics);
    }

    /// <summary>
    /// Falling off a decoded body is reported while its readable stack column remains available.
    /// </summary>
    [TestMethod]
    public void MissingDecodedReturn_IsAnError()
    {
        var method = Body((_, _, il, _) => il.Emit(OpCodes.Nop));
        var column = StackAnalysis.Run(method, out var diagnostics);
        Assert.Contains("[]", column);
        Assert.Contains(diagnostic => diagnostic.Code == "FLOW020" && diagnostic.Kind == AnalysisDiagnosticKind.Error, diagnostics);
    }

    /// <summary>
    /// Decoded unaligned prefixes accept only the three alignments defined by ECMA-335.
    /// </summary>
    /// <param name="alignment">The encoded alignment.</param>
    /// <param name="accepted">Whether ECMA-335 permits the value.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    [DataRow(4, true)]
    [DataRow(255, false)]
    public void UnalignedPrefix_RequiresPermittedAlignment(int alignment, bool accepted)
    {
        var method = Body((module, _, il, body) =>
        {
            body.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
            il.Emit(OpCodes.Ldloca_S, body.Body.Variables[0]);
            il.Emit(OpCodes.Unaligned, (byte)alignment);
            il.Emit(OpCodes.Ldind_I4);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);
        });

        StackAnalysis.Run(method, out var diagnostics);
        Assert.AreEqual(accepted, diagnostics.All(diagnostic => diagnostic.Code != "FLOW019"));
    }
}
