using System.Threading.Channels;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Method comparisons execute captured originals and copies in real independent host processes.
/// </summary>
[TestClass]
public sealed class MethodComparisonTests
{
    /// <summary>
    /// The cancellation token supplied to real worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Literal calls report typed inputs and returns from both executable versions.
    /// </summary>
    /// <param name="change">Whether multiplication becomes addition in the copy.</param>
    /// <param name="outcome">The expected comparison outcome.</param>
    /// <param name="editedValue">The expected edited return value.</param>
    [TestMethod]
    [DataRow(false, "match", "-10")]
    [DataRow(true, "different", "-3")]
    public async Task Run_LiteralCallsObserveTypedValues(bool change, string outcome, string editedValue)
    {
        var session = IlLines.Load(".method int32 Double(int32 value) { ldarg.0; ldc.i4.2; mul; ret }");
        var edit = Commit(session, "Double", change ? "mul" : null, change ? "add" : null);
        var package = ComparisonCapture.Create(session, "Copy (-5) --assert");

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual(outcome, result.Outcome, Details(result));
        Assert.AreEqual(edit.Fingerprint, result.BaselineFingerprint);
        Assert.AreEqual(1, result.Revision);
        Assert.IsTrue(package.Assert);
        Assert.AreEqual("completed", result.Original.Outcome, Details(result));
        Assert.AreEqual("completed", result.Edited.Outcome, Details(result));
        AssertScalar(result.Original.Result!, "System.Int32", "-10");
        AssertScalar(result.Edited.Result!, "System.Int32", editedValue);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
        AssertScalar(result.Original.Invocations[0].Inputs.Single(member => member.Name == "argument 0").Value,
            "System.Int32", "-5");
        AssertScalar(result.Edited.Invocations[0].Outputs.Single(member => member.Name == "return").Value,
            "System.Int32", editedValue);
        Assert.AreEqual(-10, session.Methods.Single().Version.Body.Invoke(null, [-5]));
    }

    /// <summary>
    /// Differences in standard output and standard error remain observable when return values match.
    /// </summary>
    [TestMethod]
    public async Task Run_ConsoleStreamsDistinguishEqualReturnValues()
    {
        var session = IlLines.Load(".method int32 Print() {", "ldstr \"before\"", "call void Console::Write(string)",
            "call class System.IO.TextWriter Console::get_Error()", "ldstr \"error\"",
            "callvirt instance void System.IO.TextWriter::Write(string)", "ldc.i4 42", "ret", "}");
        Commit(session, "Print", "\"before\"", "\"after\"");

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("different", result.Outcome, Details(result));
        Assert.AreEqual("before", result.Original.StandardOutput);
        Assert.AreEqual("after", result.Edited.StandardOutput);
        Assert.AreEqual("error", result.Original.StandardError);
        Assert.AreEqual("error", result.Edited.StandardError);
        AssertScalar(result.Original.Result!, "System.Int32", "42");
        AssertScalar(result.Edited.Result!, "System.Int32", "42");
    }

    /// <summary>
    /// Exceptions preserve their actual type, stored message, and HRESULT without reflection invocation wrappers.
    /// </summary>
    /// <param name="change">Whether the copied method throws a different message.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_ExceptionsAreComparedAsObservedFailures(bool change)
    {
        var session = IlLines.Load(".method int32 Fail() {", "ldstr \"original failure\"",
            "newobj instance void InvalidOperationException::.ctor(string)", "throw", "}");
        Commit(session, "Fail", change ? "original failure" : null, change ? "edited failure" : null);

        var result = await Run(session, "Copy ()");

        Assert.AreEqual(change ? "different" : "match", result.Outcome, Details(result));
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.IsNotNull(result.Original.Exception);
        Assert.EndsWith("System.InvalidOperationException", result.Original.Exception.Type);
        Assert.AreEqual("original failure", result.Original.Exception.Message);
        Assert.AreEqual(new InvalidOperationException().HResult, result.Original.Exception.HResult);
        Assert.IsNotNull(result.Edited.Exception);
        Assert.AreEqual(change ? "edited failure" : "original failure", result.Edited.Exception.Message);
        Assert.AreEqual(result.Original.Exception, result.Original.Invocations.Single().Exception);
        Assert.AreEqual(result.Edited.Exception, result.Edited.Invocations.Single().Exception);
    }

    /// <summary>
    /// By-reference arguments are captured before and after real writes by each selected method.
    /// </summary>
    [TestMethod]
    public async Task Run_ReferenceArgumentsPreserveInputAndObserveMutation()
    {
        var session = IlLines.Load(".method int32 Increment(int32& value) {", "ldarg.0", "dup", "ldind.i4",
            "ldc.i4.1", "add", "stind.i4", "ldarg.0", "ldind.i4", "ret", "}");
        Commit(session, "Increment", "ldc.i4.1", "ldc.i4.2");

        var result = await Run(session, "Copy (41)");

        Assert.AreEqual("different", result.Outcome, Details(result));
        var before = result.Original.Invocations.Single();
        var after = result.Edited.Invocations.Single();
        AssertScalar(before.Inputs.Single(member => member.Name == "argument 0").Value, "System.Int32", "41");
        AssertScalar(after.Inputs.Single(member => member.Name == "argument 0").Value, "System.Int32", "41");
        AssertScalar(before.Outputs.Single(member => member.Name == "argument 0").Value, "System.Int32", "42");
        AssertScalar(after.Outputs.Single(member => member.Name == "argument 0").Value, "System.Int32", "43");
        AssertScalar(result.Original.Result!, "System.Int32", "42");
        AssertScalar(result.Edited.Result!, "System.Int32", "43");
    }

    /// <summary>
    /// Each repeated instance call reports its own receiver state immediately before and after that call.
    /// </summary>
    [TestMethod]
    public async Task Run_InstanceScenarioSnapshotsEachInvocationBoundary()
    {
        var session = IlLines.Load(".class public Counter {", ".field public int32 Value",
            ".method public instance void .ctor(int32 value) {", "ldarg.0", "call instance void Object::.ctor()",
            "ldarg.0", "ldarg.1", "stfld int32 Counter::Value", "ret", "}",
            ".method public instance int32 Next() {", "ldarg.0", "dup", "ldfld int32 Counter::Value", "ldc.i4.1",
            "add", "stfld int32 Counter::Value", "ldarg.0", "ldfld int32 Counter::Value", "ret", "}", "}");
        var edit = Commit(session, "instance int32 Counter::Next()");
        var owner = edit.Method!.DeclaringType!.FullName!;
        foreach (var line in new[]
        {
            ".method int32 Scenario() {",
            $".locals init (class {owner} counter)",
            "ldc.i4 40",
            $"newobj instance void {owner}::.ctor(int32)",
            "stloc.0",
            "ldloc.0",
            $"call instance int32 {owner}::Next()",
            "pop",
            "ldloc.0",
            $"call instance int32 {owner}::Next()",
            "ret",
            "}",
        })
        {
            session.AddLine(line);
        }

        var result = await Run(session, "Copy using Scenario");

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.HasCount(2, side.Invocations);
            AssertScalar(side.Result!, "System.Int32", "42");
            AssertReceiver(side.Invocations[0].Inputs, "40");
            AssertReceiver(side.Invocations[0].Outputs, "41");
            AssertReceiver(side.Invocations[1].Inputs, "41");
            AssertReceiver(side.Invocations[1].Outputs, "42");
        }
    }

    /// <summary>
    /// A scenario that invokes the selected method a different number of times reports different inputs explicitly.
    /// </summary>
    [TestMethod]
    public async Task Run_ScenarioCallCountDifferencesAreDifferentInputs()
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.1; ret }");
        Commit(session, "Value", "ldc.i4.1", "ldc.i4.2");
        foreach (var line in IlLines.Expand(".method int32 Scenario() { call int32 Copy(); dup; ldc.i4.1; bne.un DONE;"
            + " pop; call int32 Copy(); DONE: ret }"))
        {
            session.AddLine(line);
        }

        var result = await Run(session, "Copy using Scenario");

        Assert.AreEqual("different-inputs", result.Outcome, Details(result));
        Assert.HasCount(2, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
        AssertScalar(result.Original.Result!, "System.Int32", "1");
        AssertScalar(result.Edited.Result!, "System.Int32", "2");
    }

    /// <summary>
    /// A scenario that never calls the selected method remains incomplete even when both scenarios return the same value.
    /// </summary>
    [TestMethod]
    public async Task Run_ZeroSelectedCallsCannotProduceMatch()
    {
        var session = IlLines.Load(".method int32 Value() { ldc.i4.1; ret }", ".method int32 Scenario() { ldc.i4 42; ret }");
        Commit(session, "Value");

        var result = await Run(session, "Copy using Scenario");

        Assert.AreEqual("incomplete", result.Outcome, Details(result));
        Assert.AreEqual("setup-failed", result.Original.Outcome);
        Assert.AreEqual("setup-failed", result.Edited.Outcome);
        Assert.IsEmpty(result.Original.Invocations);
        Assert.IsEmpty(result.Edited.Invocations);
        Assert.Contains("did not invoke", result.Original.Detail!);
    }

    /// <summary>
    /// Real asynchronous Task and ValueTask results are awaited before their integer results are observed.
    /// </summary>
    /// <param name="valueTask">Whether the method wraps its Task in a ValueTask.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_TaskAndValueTaskResultsAreAwaited(bool valueTask)
    {
        var session = new Session();
        session.Resolver.Load(SampleHost.Samples.FixturesDll);
        var task = "class System.Threading.Tasks.Task`1<int32>";
        var returned = valueTask ? "valuetype System.Threading.Tasks.ValueTask`1<int32>" : task;
        session.AddLine($".method {returned} Later() {{");
        session.AddLine($"call {task} [Fixtures]Fixtures.Shapes::Later()");
        if (valueTask)
        {
            session.AddLine("newobj instance void valuetype System.Threading.Tasks.ValueTask`1<int32>::.ctor("
                + "class System.Threading.Tasks.Task`1<!0>)");
        }

        session.AddLine("ret");
        session.AddLine("}");
        Commit(session, "Later");

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("match", result.Outcome, Details(result));
        AssertScalar(result.Original.Result!, "System.Int32", "42");
        AssertScalar(result.Edited.Result!, "System.Int32", "42");
        AssertScalar(result.Original.Invocations.Single().Outputs.Single(member => member.Name == "return").Value,
            "System.Int32", "42");
        AssertScalar(result.Edited.Invocations.Single().Outputs.Single(member => member.Name == "return").Value,
            "System.Int32", "42");
    }

    /// <summary>
    /// The instrumentation consumes real pooled channel value tasks exactly once and observes their actual completion.
    /// </summary>
    /// <param name="fail">Whether the pending channel operation fails.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Run_PooledValueTaskIsConsumedOnce(bool fail)
    {
        var consumed = ComparisonAsyncSource.Start(false);
        Assert.AreEqual(42, await consumed);
        Assert.ThrowsExactly<InvalidOperationException>(() => consumed.GetAwaiter().GetResult());
        var expectedFailure = fail ? await Assert.ThrowsExactlyAsync<ChannelClosedException>(async () =>
        {
            await ComparisonAsyncSource.Start(true);
        }) : null;

        var session = new Session();
        session.Resolver.Load(typeof(ComparisonAsyncSource).Assembly.Location);
        session.AddLine(".method valuetype System.Threading.Tasks.ValueTask`1<int32> Read(bool fail) {");
        session.AddLine("ldarg.0");
        session.AddLine("call valuetype System.Threading.Tasks.ValueTask`1<int32> "
            + "[IlRepl.Tests]IlRepl.Tests.Engine.ComparisonAsyncSource::Start(bool)");
        session.AddLine("ret");
        session.AddLine("}");
        Commit(session, "Read");

        var result = await Run(session, fail ? "Copy (true)" : "Copy (false)");

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, Details(result));
            var invocation = side.Invocations.Single();
            if (fail)
            {
                Assert.IsNotNull(side.Exception);
                Assert.EndsWith("System.Threading.Channels.ChannelClosedException", side.Exception.Type);
                Assert.AreEqual(expectedFailure!.Message, side.Exception.Message);
                Assert.IsNotNull(side.Exception.Inner);
                Assert.EndsWith("System.InvalidOperationException", side.Exception.Inner.Type);
                Assert.AreEqual("channel failed", side.Exception.Inner.Message);
                Assert.IsNotNull(invocation.Exception);
                Assert.IsNotNull(invocation.Exception.Inner);
                // The task is another root in the invocation snapshot, so exception identities differ.
                Assert.AreEqual(side.Exception.Inner with { Identity = invocation.Exception.Inner.Identity }, invocation.Exception.Inner);
                Assert.AreEqual(side.Exception with { Identity = invocation.Exception.Identity, Inner = invocation.Exception.Inner },
                    invocation.Exception);
            }
            else
            {
                AssertScalar(side.Result!, "System.Int32", "42");
                AssertScalar(invocation.Outputs.Single(member => member.Name == "return").Value, "System.Int32", "42");
            }
        }
    }

    /// <summary>
    /// Out-only arguments do not expose their caller's prior value as input and retain the actual value written on return.
    /// </summary>
    [TestMethod]
    public async Task Run_OutArgumentHasNoInputValueAndCapturesItsWrite()
    {
        var session = IlLines.Load(".class public Writer {",
            ".method public static void Set([out] int32& value) { ldarg.0; ldc.i4 42; stind.i4; ret }", "}");
        var edit = Commit(session, "void Writer::Set(int32&)");
        Assert.IsTrue(edit.Method!.GetParameters()[0].IsOut);
        foreach (var line in new[]
        {
            ".method int32 Scenario() {",
            ".locals init (int32 value)",
            "ldc.i4 123",
            "stloc.0",
            "ldloca.s 0",
            "call void Copy(int32&)",
            "ldloc.0",
            "ret",
            "}",
        })
        {
            session.AddLine(line);
        }

        var result = await Run(session, "Copy using Scenario");

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            AssertScalar(side.Result!, "System.Int32", "42");
            var invocation = side.Invocations.Single();
            Assert.AreEqual("null", invocation.Inputs.Single(member => member.Name == "argument 0").Value.Kind);
            AssertScalar(invocation.Outputs.Single(member => member.Name == "argument 0").Value, "System.Int32", "42");
        }
    }

    private Task<ComparisonReply> Run(Session session, string command) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, command), TestContext.CancellationToken);

    private static MethodEdit Commit(Session session, string reference, string? before = null, string? after = null)
    {
        var edit = session.PrepareEdit(reference, "Copy");
        return session.CommitEdit(edit.Name, before is null ? edit.Source : edit.Source.Replace(before, after!, StringComparison.Ordinal));
    }

    private static void AssertScalar(ObservedValue value, string typeSuffix, string scalar)
    {
        Assert.AreEqual("scalar", value.Kind);
        Assert.EndsWith(typeSuffix, value.Type);
        Assert.AreEqual(scalar, value.Value);
    }

    private static void AssertReceiver(IReadOnlyList<ObservedMember> roots, string value)
    {
        var receiver = roots.Single(member => member.Name == "receiver").Value;
        Assert.AreEqual("object", receiver.Kind);
        AssertScalar(receiver.Members.Single(member => member.Name.EndsWith("::Value", StringComparison.Ordinal)).Value,
            "System.Int32", value);
    }

    private static string Details(ComparisonReply result) =>
        $"{result.Outcome}: original={result.Original.Outcome} {result.Original.Detail}; "
        + $"edited={result.Edited.Outcome} {result.Edited.Detail}";
}
