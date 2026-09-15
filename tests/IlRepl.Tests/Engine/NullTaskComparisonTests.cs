using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Comparison instrumentation preserves null task returns and the caller's resulting control flow.
/// </summary>
[TestClass]
public sealed class NullTaskComparisonTests
{
    /// <summary>
    /// Supplies cancellation for the actual comparison worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Direct calls observe a null return without inventing an asynchronous failure.
    /// </summary>
    /// <param name="generic">Whether the declared return type is Task of int32.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Direct_NullTask_RemainsNullWithoutAnException(bool generic)
    {
        var session = Create(generic, change: false);

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            AssertCompleted(side);
            Assert.AreEqual("null-task", side.Result!.Kind);
            Assert.AreEqual("null-task", side.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Kind);
        }
    }

    /// <summary>
    /// A scenario takes the null branch only when the selected method actually returns null.
    /// </summary>
    /// <param name="generic">Whether the declared return type is Task of int32.</param>
    /// <param name="change">Whether the edit returns a completed task instead.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Scenario_NullTask_PreservesTheCallersBranch(bool generic, bool change)
    {
        var session = Create(generic, change);
        foreach (var line in IlLines.Expand(".method int32 Scenario() {", "call Copy", "brtrue.s present",
            "ldc.i4.s 41", "ret", "present: ldc.i4.s 42", "ret", "}"))
        {
            session.AddLine(line);
        }

        var result = await Run(session, "Copy using Scenario");

        Assert.AreEqual(change ? "different" : "match", result.Outcome, Details(result));
        AssertCompleted(result.Original);
        AssertCompleted(result.Edited);
        Assert.AreEqual("41", result.Original.Result!.Value);
        Assert.AreEqual(change ? "42" : "41", result.Edited.Result!.Value);
        Assert.AreEqual("null-task", result.Original.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Kind);
    }

    /// <summary>
    /// Arrays of tasks are returned as arrays rather than passed to a task tracker.
    /// </summary>
    /// <param name="generic">Whether each array element is Task of int32.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Direct_TaskArray_RemainsAnArray(bool generic)
    {
        var task = "class System.Threading.Tasks.Task" + (generic ? "`1<int32>" : "");
        var session = IlLines.Load(".method " + task + "[] Read() {", "ldc.i4.0", "newarr " + task, "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            AssertCompleted(side);
            Assert.AreEqual("array", side.Result!.Kind);
            Assert.AreEqual("array", side.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Kind);
        }
    }

    /// <summary>
    /// A reference to null task storage retains its reference alias without being awaited.
    /// </summary>
    /// <param name="generic">Whether the referenced value is Task of int32.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Direct_ReferenceToNullTask_PreservesStorage(bool generic)
    {
        var task = "class System.Threading.Tasks.Task" + (generic ? "`1<int32>" : "");
        var session = IlLines.Load(".method " + task + "& Read(" + task + "& pending) {", "ldarg.0", "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);

        var result = await Run(session, "Copy (null)");

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            AssertCompleted(side);
            Assert.AreEqual("null", side.Result!.Kind);
            var outputs = side.Invocations.Single().Outputs;
            Assert.AreEqual("null", outputs.Single(member => member.Name == "return").Value.Kind);
            Assert.AreEqual("-1,1,1", outputs.Single(member => member.Name == "reference aliases").Value.Value);
        }
    }

    /// <summary>
    /// Signature modifiers preserve observation of a generic task's completed result.
    /// </summary>
    /// <param name="modifier">The required or optional return modifier.</param>
    /// <param name="valueTask">Whether the method returns a value task.</param>
    [TestMethod]
    [DataRow("modopt", false)]
    [DataRow("modreq", false)]
    [DataRow("modopt", true)]
    [DataRow("modreq", true)]
    public async Task Direct_ModifiedTaskReturn_ObservesTheCompletedResult(string modifier, bool valueTask)
    {
        var task = (valueTask ? "valuetype System.Threading.Tasks.ValueTask`1<int32> " : "class System.Threading.Tasks.Task`1<int32> ")
            + modifier + "(System.Runtime.CompilerServices.IsConst)";
        var body = valueTask ? "ldc.i4.s 42\nnewobj instance void valuetype System.Threading.Tasks.ValueTask`1<int32>::.ctor(!0)"
            : "ldc.i4.s 42\ncall class System.Threading.Tasks.Task`1<!!0> System.Threading.Tasks.Task::FromResult<int32>(!!0)";
        var session = IlLines.Load((".method " + task + " Read() {\n" + body + "\nret\n}").Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldc.i4.s 42", "ldc.i4.s 43", StringComparison.Ordinal));

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("different", result.Outcome, Details(result));
        AssertCompleted(result.Original);
        AssertCompleted(result.Edited);
        Assert.AreEqual("42", result.Original.Result!.Value);
        Assert.AreEqual("43", result.Edited.Result!.Value);
        Assert.AreEqual("42", result.Original.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Value);
        Assert.AreEqual("43", result.Edited.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Value);
    }

    private static Session Create(bool generic, bool change)
    {
        var task = "class System.Threading.Tasks.Task" + (generic ? "`1<int32>" : "");
        var session = IlLines.Load(".method " + task + " Read() {", "ldnull", "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        var body = generic
            ? "ldc.i4.s 42\ncall class System.Threading.Tasks.Task`1<!!0> System.Threading.Tasks.Task::FromResult<int32>(!!0)"
            : "call class System.Threading.Tasks.Task System.Threading.Tasks.Task::get_CompletedTask()";
        session.CommitEdit(edit.Name, change ? edit.Source.Replace("ldnull", body, StringComparison.Ordinal) : edit.Source);
        Assert.IsNull(edit.OriginalMethod.Invoke(null, null));
        return session;
    }

    private Task<ComparisonReply> Run(Session session, string command) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, command), TestContext.CancellationToken);

    private static void AssertCompleted(ComparisonSide side)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.HasCount(1, side.Invocations);
        Assert.IsNull(side.Invocations[0].Exception);
    }

    private static string Details(ComparisonReply result) => result.Original.Detail + "; " + result.Edited.Detail;
}
