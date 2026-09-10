using IlRepl.Engine;
using IlRepl.Protocol;
using ILVerify;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Compares raw metadata bodies against .NET ILVerification and executes only accepted examples.
/// </summary>
[TestClass]
public sealed class ControlFlowOracleTests
{
    /// <summary>
    /// Branch joins, loops, and dead code agree with an independent verifier before any JIT is requested.
    /// </summary>
    [TestMethod]
    [DataRow("diamond", "")]
    [DataRow("loop", "")]
    [DataRow("dead", "")]
    [DataRow("underflow", "StackUnderflow")]
    [DataRow("join", "PathStackDepth")]
    [DataRow("return", "StackUnexpected")]
    public void RawBody_AgreesWithVerifier(string shape, string expected)
    {
        var session = new Session();
        var (_, image, fixture) = CecilFixture.Build((module, type) =>
        {
            var method = new MethodDefinition("M", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            var join = il.Create(OpCodes.Ret);
            switch (shape)
            {
                case "diamond":
                    var other = il.Create(OpCodes.Ldc_I4_2);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Brtrue, other);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Br, join);
                    il.Append(other);
                    il.Append(join);
                    break;
                case "loop":
                    var again = il.Create(OpCodes.Ldarg_0);
                    var done = il.Create(OpCodes.Ldc_I4_1);
                    il.Append(again);
                    il.Emit(OpCodes.Brfalse, done);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Sub);
                    il.Emit(OpCodes.Starg, method.Parameters[0]);
                    il.Emit(OpCodes.Br, again);
                    il.Append(done);
                    il.Append(join);
                    break;
                case "dead":
                    var live = il.Create(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Br, live);
                    il.Emit(OpCodes.Pop);
                    il.Append(live);
                    il.Append(join);
                    break;
                case "underflow":
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Append(join);
                    break;
                case "join":
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Brtrue, join);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Append(join);
                    break;
                case "return":
                    il.Emit(OpCodes.Ldstr, "wrong");
                    il.Append(join);
                    break;
                default:
                    Assert.Fail("Unknown fixture shape.");
                    break;
            }
        }, session.Resolver);
        using var oracle = new IlVerificationOracle();
        var errors = oracle.Verify(image);
        var method = fixture.GetMethod("M")!;
        var diagnostics = StackAnalysis.Diagnostics(MethodDisassembler.Disassemble(method, session));
        var valid = expected.Length == 0;
        Assert.AreSequenceEqual(expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(),
            errors.Select(error => error.ToString()).Distinct().Order(), string.Join(", ", errors));
        Assert.AreEqual(valid, !diagnostics.Any(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error),
            string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
        if (valid)
        {
            Assert.AreEqual(shape == "diamond" ? 2 : 1, method.Invoke(null, [2]));
        }
    }

    /// <summary>
    /// Stack allocation remains executable while its unverifiable status is shown separately from correctness errors.
    /// </summary>
    [TestMethod]
    public void StackAllocation_IsAllowedAndIdentified()
    {
        var session = new Session();
        var (_, image, fixture) = CecilFixture.Build((module, type) =>
        {
            var method = new MethodDefinition("M", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4_4);
            il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Localloc);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        }, session.Resolver);
        using var oracle = new IlVerificationOracle();
        Assert.Contains(VerifierError.Unverifiable, oracle.Verify(image));
        var method = fixture.GetMethod("M")!;
        var diagnostics = StackAnalysis.Diagnostics(MethodDisassembler.Disassemble(method, session));
        Assert.Contains(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Unverifiable, diagnostics);
        Assert.DoesNotContain(diagnostic => diagnostic.Kind == AnalysisDiagnosticKind.Error, diagnostics);
        Assert.AreEqual(1, method.Invoke(null, null));
    }

    /// <summary>
    /// Missing reference metadata cannot be mistaken for the expected rejection of a malformed stack.
    /// </summary>
    [TestMethod]
    public void UnresolvedMetadata_FailsTheOracle()
    {
        var (_, image, _) = CecilFixture.Build((module, type) =>
        {
            var missing = new AssemblyNameReference("IlReplMissingVerificationReference", new Version(1, 0));
            module.AssemblyReferences.Add(missing);
            var owner = new TypeReference("Missing", "Library", module, missing);
            var target = new MethodReference("F", module.TypeSystem.Void, owner);
            var method = new MethodDefinition("M", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
        });
        using var oracle = new IlVerificationOracle();
        var error = Assert.ThrowsExactly<FileNotFoundException>(() => oracle.Verify(image));
        Assert.Contains("IlReplMissingVerificationReference", error.Message);
    }
}
