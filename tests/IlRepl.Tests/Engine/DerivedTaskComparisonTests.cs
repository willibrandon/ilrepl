using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real comparison processes observe derived tasks after completion without replacing them.
/// </summary>
[TestClass]
public sealed class DerivedTaskComparisonTests
{
    /// <summary>
    /// Supplies cancellation for the comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Pending subclasses retain identity and report eventual results, failures, and input mutations.
    /// </summary>
    /// <param name="kind">The non-generic, generic, or generically derived task shape.</param>
    /// <param name="fail">Whether completion throws after mutating the input.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public async Task Scenario_DerivedTask_ObservesCompletion(int kind, bool fail)
    {
        var session = Create(kind, fail);

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var (side, expected) in new[] { (result.Original, "42"), (result.Edited, "43") })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            var invocation = side.Invocations.Single();
            Assert.AreEqual("0", invocation.Inputs.Single(member => member.Name == "argument 0").Value.Members[0].Value.Value);
            Assert.AreEqual(expected, invocation.Outputs.Single(member => member.Name == "argument 0").Value.Members[0].Value.Value);
            if (fail)
            {
                Assert.IsNotNull(side.Exception);
                Assert.EndsWith("InvalidOperationException", side.Exception.Type);
                Assert.AreEqual("derived failure", side.Exception.Message);
                Assert.IsNotNull(invocation.Exception);
                // Each snapshot numbers its own roots, including the invocation's arguments and task.
                Assert.AreEqual(side.Exception with { Identity = invocation.Exception.Identity }, invocation.Exception);
            }
            else
            {
                Assert.IsNull(side.Exception);
                Assert.IsNull(invocation.Exception);
                Assert.AreEqual(kind == 0 ? "null" : "scalar", side.Result!.Kind);
                Assert.AreEqual(kind == 0 ? null : expected, side.Result.Value);
                var returned = invocation.Outputs.Single(member => member.Name == "return").Value;
                Assert.AreEqual(side.Result.Kind, returned.Kind);
                Assert.AreEqual(side.Result.Value, returned.Value);
            }
        }
    }

    /// <summary>
    /// A scenario cannot report a complete comparison while its derived task remains pending.
    /// </summary>
    /// <param name="kind">The derived task shape.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Scenario_UnfinishedDerivedTask_RemainsIncomplete(int kind)
    {
        var session = Create(kind, fail: false, complete: false);

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("incomplete", result.Outcome);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("setup-failed", side.Outcome);
            Assert.AreEqual("a selected-method invocation was still running when its scenario completed", side.Detail);
            Assert.IsEmpty(side.Invocations);
        }
    }

    /// <summary>
    /// A null subclass return remains null instead of registering a task completion.
    /// </summary>
    /// <param name="kind">The derived task shape.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Direct_NullDerivedTask_RemainsNull(int kind)
    {
        var session = IlLines.Load(DerivedTaskComparisonExamples.Source(kind).Split('\n'));
        var task = kind == 2 ? "class Work`1<int32>" : "class Work";
        foreach (var line in IlLines.Expand(".method " + task + " Read() {", "ldnull", "ret", "}"))
        {
            session.AddLine(line);
        }

        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);

        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual("null-task", side.Result!.Kind);
            var invocation = side.Invocations.Single();
            Assert.IsNull(invocation.Exception);
            Assert.AreEqual("null-task", invocation.Outputs.Single(member => member.Name == "return").Value.Kind);
        }
    }

    /// <summary>
    /// A subclass from an external assembly is recognized through its Task inheritance.
    /// </summary>
    [TestMethod]
    public async Task Scenario_ExternalDerivedTask_ObservesItsResult()
    {
        const string task = "class [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonDerivedTask`1<int32>";
        var session = new Session();
        session.Resolver.Load(typeof(ComparisonDerivedTask<>).Assembly.Location);
        var source = $$"""
            .class public Owner {
              .field public static int32 Number
              .method public static int32 Finish() {
                ldsfld int32 Owner::Number
                ret
              }
              .method public static {{task}} Read() {
                ldc.i4.s 42
                stsfld int32 Owner::Number
                ldnull
                ldftn int32 Owner::Finish()
                newobj instance void class Func`1<int32>::.ctor(object, native int)
                newobj instance void {{task}}::.ctor(class Func`1<!0>)
                ret
              }
            }
            """;
        foreach (var line in source.Split('\n'))
        {
            session.AddLine(line);
        }

        var edit = session.PrepareEdit(task + " Owner::Read()", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 42", "ldc.i4.s 43", StringComparison.Ordinal));
        foreach (var line in IlLines.Expand(".method class Task Scenario() {", "call Copy", "dup",
            "callvirt instance void Task::RunSynchronously()", "ret", "}"))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var (side, expected) in new[] { (result.Original, "42"), (result.Edited, "43") })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual(expected, side.Result!.Value);
            var invocation = side.Invocations.Single();
            Assert.IsNull(invocation.Exception);
            Assert.AreEqual(expected, invocation.Outputs.Single(member => member.Name == "return").Value.Value);
        }
    }

    /// <summary>
    /// Assembly-qualified generic signatures resolve against the active session's loaded types.
    /// </summary>
    [TestMethod]
    public void GenericReturnType_ResolvesItsSessionAssembly()
    {
        var session = IlLines.Load(DerivedTaskComparisonExamples.Source(2).Split('\n'));
        var expected = session.TypeTable.Entries.Single(entry => entry.Type.IsGenericTypeDefinition).Type;

        var resolved = session.Resolver.Resolve("Work`1", expected.Assembly.GetName().Name, session.InspectionContext);

        Assert.AreSame(expected, resolved);
        Assert.AreSame(expected.Assembly, resolved.Assembly);
    }

    private static Session Create(int kind, bool fail, bool complete = true)
    {
        var session = IlLines.Load(DerivedTaskComparisonExamples.Source(kind, fail).Split('\n'));
        var edit = session.PrepareEdit((kind == 2 ? "class Work`1<int32>" : "class Work") + " Owner::Read(int32[])", "Copy");
        session.CommitEdit(edit.Name, DerivedTaskComparisonExamples.Method(kind, 43));
        foreach (var line in DerivedTaskComparisonExamples.Scenario(complete).Split('\n'))
        {
            session.AddLine(line);
        }

        return session;
    }
}
