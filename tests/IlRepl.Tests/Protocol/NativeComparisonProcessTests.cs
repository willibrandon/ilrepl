using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies real native comparisons distinguish code changes from process-specific address values.
/// </summary>
[TestClass]
public sealed class NativeComparisonProcessTests
{
    /// <summary>
    /// Supplies cancellation to each independent worker pair.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Equivalent instruction bodies compiled in separate workers produce equal normal views without any execution.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Compare_EquivalentIlInSeparateProcessesIsEqual()
    {
        using var core = new ReplCore();
        Submit(core, ".method int32 Left(int32 value) { ldarg.0; ldc.i4.2; mul; ret }",
            ".method int32 Right(int32 value) { ldarg.0; ldc.i4.2; mul; ret }");
        var package = Prepare(core, ".jit Left --against Right");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("equal", result.Outcome, Details(result));
        Assert.IsNotNull(result.Right);
        Assert.IsEmpty(result.Difference);
        Assert.AreEqual(0, result.Left.Invocations);
        Assert.AreEqual(0, result.Right.Invocations);
        var left = Assert.ContainsSingle(result.Left.Compilations);
        var right = Assert.ContainsSingle(result.Right.Compilations);
        Assert.AreEqual(left.CodeSize, right.CodeSize);
        Assert.AreSequenceEqual(left.Normalized, right.Normalized);
        Assert.Contains("Left", left.Method);
        Assert.Contains("Right", right.Method);
    }

    /// <summary>
    /// String, type, and static field references compare equal after both workers prove their own addresses.
    /// </summary>
    /// <param name="operand">The process-dependent operand whose proof is required.</param>
    /// <param name="body">The IL body that uses that operand.</param>
    [TestMethod]
    [DataRow("string", "ldstr \"native address proof\"; ret")]
    [DataRow("type", "ldtoken Owner; call class Type Type::GetTypeFromHandle(valuetype RuntimeTypeHandle); ret")]
    [DataRow("static", "ldsfld object Owner::Value; ret")]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Compare_RuntimeReferencesInSeparateProcessesAreEqual(string operand, string body)
    {
        using var core = new ReplCore();
        Submit(core, ".class public Owner { .field public static object Value }",
            ".method object Read() { " + body + " }");
        var package = Prepare(core, ".jit Read --against Read");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("equal", result.Outcome, operand + ": " + Details(result));
        Assert.IsNotNull(result.Right);
        Assert.IsEmpty(result.Difference);
        foreach (var side in new[] { result.Left, result.Right })
        {
            Assert.AreEqual("complete", side.Outcome, side.Detail);
            Assert.AreEqual(0, side.Invocations);
            Assert.IsEmpty(side.NormalizationProblems);
            var code = Assert.ContainsSingle(side.Compilations);
            Assert.Contains(line => line.Contains('<'), code.Normalized, operand + " must have a proven symbolic operand.");
            if (operand == "static")
            {
                var field = Assert.ContainsSingle(side.Addresses.Where(fact => fact.Kind == "static-field"));
                Assert.AreEqual("object Owner::Value", field.DisplaySymbol);
                Assert.Contains("Owner, ilrepl.", field.Symbol);
                Assert.Contains("Version=", field.Symbol);
                Assert.Contains("::Value:System.Object", field.Symbol);
                Assert.Contains(line => line.Contains("<static-field:" + field.Symbol, StringComparison.Ordinal), code.Normalized);
                var display = code.Normalized.Select(line => NativeSymbolDisplay.Format(line, side.Addresses)).ToArray();
                Assert.Contains(line => line.Contains("<static-field:object Owner::Value>", StringComparison.Ordinal), display);
                Assert.DoesNotContain(line => line.Contains(field.Symbol, StringComparison.Ordinal), display);
            }
        }

        Assert.AreSequenceEqual(result.Left.Compilations[0].Normalized, result.Right.Compilations[0].Normalized);
    }

    /// <summary>
    /// Large immediates that runtime diffable output would erase remain observable differences and fail assertion mode.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Compare_LargeConstantsRemainDifferentAndAssertFails()
    {
        using var core = new ReplCore();
        Submit(core, ".method int64 Left() { ldc.i8 305419896; ret }", ".method int64 Right() { ldc.i8 305419897; ret }");
        await using var engine = new InProcessEngine(core, null, ProcessNativeRunner.RunAsync);
        var prepared = await engine.HandleAsync(".jit Left --against Right --assert", TestContext.CancellationToken);
        Assert.IsTrue(prepared.Succeeded);
        Assert.IsNotNull(prepared.PendingNative);

        var result = await engine.InspectNativeAsync(prepared.PendingNative.Identity, TestContext.CancellationToken);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Native);
        Assert.AreEqual("different", result.Native.Outcome, Details(result.Native));
        Assert.IsNotNull(result.Native.Right);
        Assert.AreEqual("left: Left", result.Native.Left.Name);
        Assert.AreEqual("right: Right", result.Native.Right.Name);
        Assert.IsNotEmpty(result.Native.Difference);
        Assert.AreEqual("--- left: Left", result.Native.Difference[0]);
        Assert.AreEqual("+++ right: Right", result.Native.Difference[1]);
        Assert.Contains(line => line.StartsWith("@@ -", StringComparison.Ordinal), result.Native.Difference);
        var output = string.Join('\n', result.Lines.Select(line => line.PlainText));
        Assert.Contains("left: Left: complete; .NET", output);
        Assert.Contains("right: Right: complete; .NET", output);
        Assert.DoesNotContain("IL fingerprint:", output);
        Assert.DoesNotContain("JIT: ", output);
        Assert.IsNotNull(result.Native.Left.Implementation);
        Assert.IsNotNull(result.Native.Right.Implementation);
        Assert.AreNotEqual(Guid.Empty, result.Native.Left.ModuleVersionId);
        Assert.IsNotEmpty(result.Native.Left.Jit);
        Assert.IsNotEmpty(result.Native.Left.Settings);
        Assert.AreNotEqual(string.Join('\n', result.Native.Left.Compilations.Single().Normalized),
            string.Join('\n', result.Native.Right.Compilations.Single().Normalized));
        Assert.DoesNotContain("D1FFAB1E", string.Join('\n', result.Native.Difference));
        Assert.IsTrue(engine.Status.CellIsEmpty);
    }

    /// <summary>
    /// Each comparison side redirects the same scenario's calls to that side's selected implementation.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Compare_ScenarioUsesSelectedImplementationOnEachSide()
    {
        using var core = new ReplCore();
        Submit(core, ".method void Left() { ldstr \"left\"; call void Console::Write(string); ret }",
            ".method void Right() { ldstr \"right\"; call void Console::Write(string); ret }",
            ".method void Scenario() { call Left; ret }");
        var package = Prepare(core, ".jit Left using Scenario --against Right");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.IsNotNull(result.Right);
        Assert.AreEqual("complete", result.Left.Outcome, Details(result));
        Assert.AreEqual("complete", result.Right.Outcome, Details(result));
        Assert.AreEqual(1, result.Left.Invocations);
        Assert.AreEqual(1, result.Right.Invocations);
        Assert.AreEqual("left", result.Left.StandardOutput);
        Assert.AreEqual("right", result.Right.StandardOutput);
        Assert.Contains("Left", Assert.ContainsSingle(result.Left.Compilations).Method);
        Assert.Contains("Right", Assert.ContainsSingle(result.Right.Compilations).Method);
        Assert.AreEqual("different", result.Outcome, Details(result));
    }

    /// <summary>
    /// An edited instance scenario constructs the matching receiver and invokes each side's unmodified selected body.
    /// </summary>
    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Compare_EditedInstanceScenarioConstructsMatchingReceiverOnEachSide()
    {
        using var core = new ReplCore();
        Submit(core, ".class public Counter {", ".field public int32 Value",
            ".method public instance void .ctor(int32 value) {", "ldarg.0", "call instance void Object::.ctor()",
            "ldarg.0", "ldarg.1", "stfld int32 Counter::Value", "ret", "}",
            ".method public instance int32 Next() {", "ldarg.0", "dup", "ldfld int32 Counter::Value", "ldc.i4.1",
            "add", "stfld int32 Counter::Value", "ldarg.0", "ldfld int32 Counter::Value", "ret", "}", "}");
        var edit = core.Session.PrepareEdit("instance int32 Counter::Next()", "Copy");
        core.Session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal));
        var owner = edit.Method!.DeclaringType!.FullName!;
        Submit(core, ".method void Scenario() {", "ldc.i4.s 41", $"newobj instance void {owner}::.ctor(int32)",
            $"call instance int32 {owner}::Next()", "call void Console::Write(int32)", "ret", "}");
        var package = Prepare(core, ".diff Copy --native using Scenario");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.IsNotNull(result.Right);
        Assert.AreEqual("complete", result.Left.Outcome, Details(result));
        Assert.AreEqual("complete", result.Right.Outcome, Details(result));
        Assert.AreEqual(1, result.Left.Invocations);
        Assert.AreEqual(1, result.Right.Invocations);
        Assert.AreEqual("42", result.Left.StandardOutput);
        Assert.AreEqual("43", result.Right.StandardOutput);
        Assert.AreEqual("Copy (original)", result.Left.Name);
        Assert.AreEqual("Copy (edited)", result.Right.Name);
        Assert.AreEqual("--- Copy (original)", result.Difference[0]);
        Assert.AreEqual("+++ Copy (edited)", result.Difference[1]);
        core.ReportNative(result);
        var output = string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText));
        Assert.Contains("Copy (original): complete; .NET", output);
        Assert.Contains("Copy (edited): complete; .NET", output);
        Assert.Contains("Next", Assert.ContainsSingle(result.Left.Compilations).Method);
        Assert.Contains("Next", Assert.ContainsSingle(result.Right.Compilations).Method);
        Assert.AreEqual("different", result.Outcome, Details(result));
        Assert.IsEmpty(result.Left.StandardError);
        Assert.IsEmpty(result.Right.StandardError);
    }

    /// <summary>
    /// Direct and function-pointer calls compare equal when their independently located targets have the same identity.
    /// </summary>
    /// <param name="indirect">Whether the body calls through an explicit function pointer.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Compare_CallTargetsInSeparateProcessesAreEqual(bool indirect)
    {
        using var core = new ReplCore();
        Submit(core, ".class public Owner {",
            ".method public static int32 Target() cil managed noinlining { ldc.i4.s 42; ret }", "}",
            ".method int32 Read() {", indirect ? "ldftn int32 Owner::Target()" : "call int32 Owner::Target()",
            indirect ? "calli int32()" : "nop", "ret", "}");
        var package = Prepare(core, ".jit Read --against Read");

        var result = await ProcessNativeRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("equal", result.Outcome, Details(result));
        Assert.IsNotNull(result.Right);
        Assert.IsEmpty(result.Difference);
        Assert.IsEmpty(result.Left.NormalizationProblems);
        Assert.IsEmpty(result.Right.NormalizationProblems);
        Assert.AreSequenceEqual(Assert.ContainsSingle(result.Left.Compilations).Normalized,
            Assert.ContainsSingle(result.Right.Compilations).Normalized);
        Assert.AreEqual(0, result.Left.Invocations);
        Assert.AreEqual(0, result.Right.Invocations);
    }

    private static NativePackage Prepare(ReplCore core, string command)
    {
        var result = core.Handle(command);
        Assert.IsTrue(result.Succeeded, string.Join('\n', core.Transcript.Lines.Select(line => line.PlainText)));
        Assert.IsNotNull(result.NativePackage);
        return result.NativePackage;
    }

    private static string Details(NativeReply result) => result.Left.Outcome + ": " + result.Left.Detail + "\n"
        + string.Join('\n', result.Left.NormalizationProblems) + "\n" + result.Right?.Outcome + ": " + result.Right?.Detail + "\n"
        + string.Join('\n', result.Right?.NormalizationProblems ?? []) + "\n" + string.Join('\n', result.Difference);

    private static void Submit(ReplCore core, params string[] source)
    {
        foreach (var line in IlLines.Expand(source))
        {
            Assert.IsTrue(core.Handle(line).Succeeded, line + "\n"
                + string.Join('\n', core.Transcript.Lines.Select(item => item.PlainText)));
        }
    }
}
