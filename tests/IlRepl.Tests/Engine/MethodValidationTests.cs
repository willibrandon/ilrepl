using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for the validation that runs when a <c>.method</c> block closes: the candidate is
/// compiled into a version assembly of its own and prepared on the JIT without ever being
/// invoked, and only then bound into its trampoline.
/// </summary>
[TestClass]
public sealed class MethodValidationTests
{
    /// <summary>
    /// A body whose branches leave the stack uneven: the linear model accepts it, the JIT does not.
    /// </summary>
    private static readonly string[] BadBranch = ["ldc.i4 0", "brfalse SKIP", "ldc.i4 1", "ldc.i4 2", "pop", "SKIP: pop"];

    private static Session Load(params string[] lines)
    {
        var session = new Session();
        foreach (var line in lines)
        {
            session.AddLine(line);
        }

        return session;
    }

    /// <summary>
    /// Preparation is available on the desktop runtimes the tests run on.
    /// </summary>
    [TestMethod]
    public void MethodPreparation_IsSupported_OnDesktop()
    {
        Assert.IsTrue(MethodPreparation.IsSupported);
    }

    /// <summary>
    /// Invalid IL is reported at the brace, the block stays open, and nothing is counted.
    /// </summary>
    [TestMethod]
    public void AddLine_CloseWithBranchStackMismatch_IsRejectedAtClose()
    {
        var session = Load([".method void Bad() {", .. BadBranch]);
        var ex = Assert.ThrowsExactly<ReplException>(() => session.AddLine("}"));
        Assert.Contains("the JIT rejected method Bad", ex.Message);
        Assert.Contains("the block is still open", ex.Message);
        Assert.AreEqual("Bad", session.OpenMethod!.Name);
        Assert.IsEmpty(session.Methods);
        Assert.AreEqual(0, session.Submissions);
    }

    /// <summary>
    /// After a rejection the body can be fixed with undo and closed again.
    /// </summary>
    [TestMethod]
    public void AddLine_CorrectedMethodAfterRejection_Commits()
    {
        var session = Load([".method void Bad() {", .. BadBranch]);
        Assert.ThrowsExactly<ReplException>(() => session.AddLine("}"));
        for (var i = 0; i < BadBranch.Length; i++)
        {
            Assert.IsTrue(session.Undo());
        }

        Assert.AreEqual("Bad", session.OpenMethod!.Name, "undoing the body keeps the header");
        session.AddLine("ldc.i4 1");
        session.AddLine("pop");
        Assert.AreEqual("end of method Bad", session.AddLine("}").Message);
        Assert.AreEqual(1, session.Submissions);
        session.AddLine("call void Bad()");
        Assert.IsTrue(session.Run().IsVoid);
    }

    /// <summary>
    /// A rejected replacement leaves the previous definition callable.
    /// </summary>
    [TestMethod]
    public void AddLine_FailedReplacement_PreservesPreviousMethod()
    {
        var session = Load(".method int32 Two() {", "ldc.i4 2", "ret", "}");
        string[] replacement = [".method int32 Two() {", .. BadBranch, "ldc.i4 3", "ret"];
        foreach (var line in replacement)
        {
            session.AddLine(line);
        }

        Assert.Contains("the JIT rejected method Two", Assert.ThrowsExactly<ReplException>(() => session.AddLine("}")).Message);
        Assert.AreEqual("Two", session.OpenMethod!.Name);
        Assert.AreEqual(1, session.Submissions);
        Assert.IsTrue(session.AbandonMethod());
        session.AddLine("call int32 Two()");
        Assert.AreEqual(2, session.Run().Value);
    }

    /// <summary>
    /// Closing a block validates the body without running it.
    /// </summary>
    [TestMethod]
    public void AddLine_MethodClose_NeverRunsTheBody()
    {
        var variable = "ILREPL_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            var session = Load(
                ".method void Mark() {",
                $"ldstr \"{variable}\"",
                "ldstr \"ran\"",
                "call void Environment::SetEnvironmentVariable(string, string)",
                "newobj instance void InvalidOperationException::.ctor()",
                "throw",
                "}");
            Assert.HasCount(1, session.Methods);
            Assert.IsNull(Environment.GetEnvironmentVariable(variable), "the body must not run at the close");

            session.AddLine("call void Mark()");
            Assert.ThrowsExactly<CellException>(() => session.Run());
            Assert.AreEqual("ran", Environment.GetEnvironmentVariable(variable), "the body runs when called");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// Without preparation, the emission check alone accepts the uneven branch. This is the
    /// browser's behavior, where Mono's PrepareMethod does nothing and the JIT speaks at the first call.
    /// </summary>
    [TestMethod]
    public void CompileMethod_WithoutPreparation_AcceptsBranchMismatch()
    {
        var signature = new MethodSignature("Bad", typeof(void), []);
        var state = new CellState(new TypeResolver(), GenericContext.Empty, [signature], signature, braceOpen: true);
        foreach (var line in BadBranch)
        {
            state.Apply(line);
        }

        state.ValidateMethodEnd();
        var trampoline = MethodTrampoline.Create(signature);
        var trampolines = new Dictionary<string, MethodTrampoline>(StringComparer.Ordinal) { ["Bad"] = trampoline };

        var version = DefinitionCompiler.CompileMethod(signature, state, trampoline, trampolines, prepare: false);
        Assert.IsNotNull(version.Implementation);
        Assert.Contains("the JIT rejected method Bad", Assert.ThrowsExactly<ReplException>(() => DefinitionCompiler.CompileMethod(signature, state, trampoline, trampolines, prepare: true)).Message);
    }

    /// <summary>
    /// A version superseded by a compatible redefinition is released and collected; the
    /// trampoline and the current version stay.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Redefinitions_ReleaseSupersededVersions()
    {
        TestSkip.Unless(!OperatingSystem.IsBrowser(), "unloading needs CoreCLR");
        const int Redefinitions = 20;
        var before = VersionAssemblies();
        var session = new Session();
        for (var i = 0; i <= Redefinitions; i++)
        {
            session.AddLine(".method int32 M() {");
            session.AddLine($"ldc.i4 {i}");
            session.AddLine("ret");
            session.AddLine("}");
        }

        Assert.HasCount(1, session.Methods);
        session.AddLine("call int32 M()");
        Assert.AreEqual(Redefinitions, session.Run().Value);

        var after = int.MaxValue;
        for (var round = 0; round < 20 && after - before >= Redefinitions; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            after = VersionAssemblies();
        }

        Assert.IsLessThan(before + Redefinitions, after, "superseded versions should be collected once the trampoline is rebound");
        Assert.AreEqual(Redefinitions, session.Methods[0].Trampoline.Method.Invoke(null, null), "the current version stays bound");
    }

    /// <summary>
    /// Recursion, calli, and delegates keep working after several validated closes.
    /// </summary>
    [TestMethod]
    public void Run_RecursionCalliAndDelegates_StillWork()
    {
        var session = Load(
            ".method int32 Fib(int32 n) {", "ldarg n", "ldc.i4 2", "blt BASE", "ldarg n", "ldc.i4 1", "sub", "call int32 Fib(int32)",
            "ldarg n", "ldc.i4 2", "sub", "call int32 Fib(int32)", "add", "ret", "BASE: ldarg n", "ret", "}",
            ".method void Greet(string name) {", "ldstr \"hello, \"", "ldarg name", "call string String::Concat(string, string)", "call void Console::WriteLine(string)", "ret", "}");

        session.AddLine("ldc.i4 10");
        session.AddLine("call int32 Fib(int32)");
        Assert.AreEqual(55, session.Run().Value);

        session.AddLine("ldstr \"methods\"");
        session.AddLine("call void Greet(string)");
        Assert.Contains("hello, methods", session.Run().StandardOutput);

        session.AddLine("ldc.i4 20");
        session.AddLine("ldftn int32 Fib(int32)");
        session.AddLine("calli int32(int32)");
        Assert.AreEqual(6765, session.Run().Value);

        session.AddLine("ldnull");
        session.AddLine("ldftn int32 Fib(int32)");
        session.AddLine("newobj instance void class Func`2<int32, int32>::.ctor(object, native int)");
        session.AddLine("ldc.i4 15");
        session.AddLine("callvirt instance !1 class Func`2<int32, int32>::Invoke(!0)");
        Assert.AreEqual(610, session.Run().Value);
        Assert.AreEqual(6, session.Submissions);
    }

    private static int VersionAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name?.StartsWith("ilrepl.methods.", StringComparison.Ordinal) == true);

    /// <summary>
    /// A body the runtime refuses for a reason other than invalid IL is still a recoverable
    /// close: the error names the method and the block stays open.
    /// </summary>
    [TestMethod]
    public void AddLine_CloseWithRuntimeRejection_IsReportedNotThrown()
    {
        var session = Load(".method void Bad() {", "callvirt void Console::WriteLine()");
        var ex = Assert.ThrowsExactly<ReplException>(() => session.AddLine("}"));
        Assert.Contains("rejected method Bad", ex.Message);
        Assert.Contains("the block is still open", ex.Message);
        Assert.AreEqual("Bad", session.OpenMethod!.Name);
        Assert.AreEqual(0, session.Submissions);
        Assert.IsTrue(session.Undo());
        session.AddLine("call void Console::WriteLine()");
        Assert.AreEqual("end of method Bad", session.AddLine("}").Message);
    }
}
