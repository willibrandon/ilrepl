using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Direct comparisons observe reference arguments and returns on external originals in fresh worker processes.
/// </summary>
[TestClass]
public sealed class ExternalOriginalReferenceTests
{
    /// <summary>
    /// Supplies cancellation for the real comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reference writes retain distinct argument storage, omit out inputs, and expose changes as output differences.
    /// </summary>
    /// <param name="output">Whether the second parameter is out rather than ref.</param>
    /// <param name="change">Whether the edit writes a different value.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Direct_ExternalReferenceWrites_PreserveInputsAndOutputs(bool output, bool change)
    {
        var signature = "void Write(int32& first, " + (output ? "[out] " : "") + "int32& second)";
        const string body = "ldarg.1\nldarg.0\nldind.i4\nldc.i4.1\nadd\nstind.i4\nret";
        var session = Create(signature, body, change ? body.Replace("ldc.i4.1", "ldc.i4.2", StringComparison.Ordinal) : body);

        var result = await Run(session, "Copy (41, 41)");

        AssertCompleted(result, change ? "different" : "match");
        foreach (var side in new[] { result.Original, result.Edited })
        {
            var invocation = side.Invocations.Single();
            AssertAliases(invocation.Inputs, "-1,1,2");
            AssertAliases(invocation.Outputs, "-1,1,2,-1");
            Assert.AreEqual("41", Value(invocation.Inputs, "argument 0").Value);
            Assert.AreEqual(output ? "null" : "scalar", Value(invocation.Inputs, "argument 1").Kind);
            Assert.AreEqual(output ? null : "41", Value(invocation.Inputs, "argument 1").Value);
            Assert.AreEqual("41", Value(invocation.Outputs, "argument 0").Value);
            Assert.AreEqual(change && side == result.Edited ? "43" : "42", Value(invocation.Outputs, "argument 1").Value);
            Assert.IsNull(side.Exception);
        }
    }

    /// <summary>
    /// Equal returned values still distinguish which reference argument supplies the returned storage.
    /// </summary>
    /// <param name="change">Whether the edit returns the second argument's storage.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Direct_ExternalReferenceReturn_PreservesReturnAliases(bool change)
    {
        var session = Create("int32& Choose(int32& first, int32& second)", "ldarg.0\nret",
            change ? "ldarg.1\nret" : "ldarg.0\nret");

        var result = await Run(session, "Copy (41, 41)");

        AssertCompleted(result, change ? "different" : "match");
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("41", side.Result!.Value);
            AssertAliases(side.Invocations.Single().Inputs, "-1,1,2");
            AssertAliases(side.Invocations.Single().Outputs, change && side == result.Edited ? "-1,1,2,2" : "-1,1,2,1");
            Assert.IsNull(side.Exception);
        }
    }

    /// <summary>
    /// Writes made before an exception remain observable even when reflection does not copy the arguments back.
    /// </summary>
    [TestMethod]
    public async Task Direct_ExternalReferenceWriteBeforeThrow_PreservesTheWrittenValue()
    {
        const string body = "ldarg.0\nldc.i4.s 42\nstind.i4\nldstr \"failure\"\n"
            + "newobj instance void [System.Runtime]System.InvalidOperationException::.ctor(string)\nthrow";
        var session = Create("void Fail(int32& number)", body, body);

        var result = await Run(session, "Copy (41)");

        AssertCompleted(result, "match");
        foreach (var side in new[] { result.Original, result.Edited })
        {
            var invocation = side.Invocations.Single();
            AssertAliases(invocation.Inputs, "-1,1");
            AssertAliases(invocation.Outputs, "-1,1,-1");
            Assert.AreEqual("41", Value(invocation.Inputs, "argument 0").Value);
            Assert.AreEqual("42", Value(invocation.Outputs, "argument 0").Value);
            Assert.IsNotNull(invocation.Exception);
            Assert.AreEqual("failure", invocation.Exception.Message);
            Assert.IsNotNull(side.Exception);
            Assert.AreEqual("failure", side.Exception.Message);
        }
    }

    /// <summary>
    /// A mismatched source module fails before an instrumented original can execute.
    /// </summary>
    [TestMethod]
    public async Task Direct_ExternalOriginalModuleMismatch_RejectsTheInvocation()
    {
        var session = Create("int32& Choose(int32& first)", "ldarg.0\nret", "ldarg.0\nret");
        var package = ComparisonCapture.Create(session, "Copy (41)");
        package = package with { Original = package.Original with { OriginalModule = Guid.NewGuid() } };

        var result = await ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);

        Assert.AreEqual("setup-failed", result.Original.Outcome);
        Assert.IsEmpty(result.Original.Invocations);
        Assert.AreEqual("the original assembly no longer matches the captured module", result.Original.Detail);
    }

    private static Session Create(string signature, string original, string edited)
    {
        var name = "ExternalReferences" + Guid.NewGuid().ToString("N");
        var source = ".assembly extern System.Runtime {}\n.assembly " + name + " {}\n.module " + name + ".dll\n"
            + ".class public N.Fixture extends [System.Runtime]System.Object {\n"
            + ".method private static void Native() runtime managed internalcall {}\n"
            + ".method public static " + signature + " cil managed {\n" + original
            + "\ncall void N.Fixture::Native()\nldnull\nthrow\n}\n}";
        var session = new Session();
        var assembly = session.Resolver.LoadImage(IlasmLocator.Assemble(source));
        var method = assembly.GetType("N.Fixture")!.GetMethods().Single(method => method.DeclaringType!.Name == "Fixture");
        var edit = session.PrepareEdit("[" + name + "]N.Fixture::" + method.Name, "Copy");
        Assert.Contains(problem => problem.Contains("Native", StringComparison.Ordinal), edit.Problems);
        Assert.IsNull(edit.Method);
        session.CommitEdit(edit.Name, ".method public static " + signature + " cil managed {\n" + edited + "\n}");
        Assert.IsEmpty(edit.Problems);
        return session;
    }

    private Task<ComparisonReply> Run(Session session, string command)
    {
        var package = ComparisonCapture.Create(session, command);
        Assert.IsNotNull(package.Original.OriginalAssembly);
        return ProcessComparisonRunner.RunAsync(package, TestContext.CancellationToken);
    }

    private static void AssertCompleted(ComparisonReply result, string outcome)
    {
        Assert.AreEqual(outcome, result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.AreEqual("completed", result.Edited.Outcome);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }

    private static void AssertAliases(IReadOnlyList<ObservedMember> values, string expected) =>
        Assert.AreEqual(expected, Value(values, "reference aliases").Value);

    private static ObservedValue Value(IReadOnlyList<ObservedMember> values, string name) =>
        values.Single(member => member.Name == name).Value;
}
