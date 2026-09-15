using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real comparison workers distinguish null task objects from successfully completed task results.
/// </summary>
[TestClass]
public sealed class TaskPresenceComparisonTests
{
    /// <summary>
    /// Supplies cancellation for the isolated comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Direct reports retain task presence while preserving the task's completed payload.
    /// </summary>
    /// <param name="kind">The non-generic, integer, or nullable string task shape.</param>
    /// <param name="originalPresent">Whether the original returns a task object.</param>
    /// <param name="editedPresent">Whether the edit returns a task object.</param>
    [TestMethod]
    [DataRow(0, false, true)]
    [DataRow(0, true, false)]
    [DataRow(0, false, false)]
    [DataRow(0, true, true)]
    [DataRow(1, false, true)]
    [DataRow(1, true, false)]
    [DataRow(2, false, true)]
    [DataRow(2, true, false)]
    [DataRow(2, true, true)]
    public async Task Direct_TaskPresence_DistinguishesNullAndCompletedReturns(int kind, bool originalPresent, bool editedPresent)
    {
        var session = await CreateAsync(kind, originalPresent, editedPresent);

        var result = await RunAsync(session, "Copy ()");

        Assert.AreEqual(originalPresent == editedPresent ? "match" : "different", result.Outcome, Details(result));
        AssertSide(result.Original, kind, originalPresent);
        AssertSide(result.Edited, kind, editedPresent);
        AssertReturnedValue(result.Original.Result!, kind, originalPresent);
        AssertReturnedValue(result.Edited.Result!, kind, editedPresent);
    }

    /// <summary>
    /// Invocation outputs reveal different task presence even when both scenarios return the same value.
    /// </summary>
    /// <param name="kind">The non-generic or nullable string task shape.</param>
    /// <param name="originalPresent">Whether the original returns a task object.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(2, false)]
    public async Task Scenario_IgnoredTaskReturn_StillObservesPresence(int kind, bool originalPresent)
    {
        var session = await CreateAsync(kind, originalPresent, !originalPresent);
        Add(session, TaskPresenceComparisonExamples.IgnoredScenario());

        var result = await RunAsync(session, "Copy using Scenario");

        Assert.AreEqual("different", result.Outcome, Details(result));
        AssertSide(result.Original, kind, originalPresent);
        AssertSide(result.Edited, kind, !originalPresent);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("scalar", side.Result!.Kind);
            Assert.AreEqual("[System.Private.CoreLib]System.Int32", side.Result.Type);
            Assert.AreEqual("42", side.Result.Value);
        }
    }

    /// <summary>
    /// Instrumented calls preserve the scenario's actual null test in both edit directions.
    /// </summary>
    /// <param name="originalPresent">Whether the original returns a task object.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scenario_TaskPresence_PreservesCallerBranch(bool originalPresent)
    {
        var session = await CreateAsync(0, originalPresent, !originalPresent);
        Add(session, TaskPresenceComparisonExamples.BranchScenario());

        var result = await RunAsync(session, "Copy using Scenario");

        Assert.AreEqual("different", result.Outcome, Details(result));
        AssertSide(result.Original, 0, originalPresent);
        AssertSide(result.Edited, 0, !originalPresent);
        Assert.AreEqual(originalPresent ? "42" : "41", result.Original.Result!.Value);
        Assert.AreEqual(originalPresent ? "41" : "42", result.Edited.Result!.Value);
    }

    private static async Task<Session> CreateAsync(int kind, bool originalPresent, bool editedPresent)
    {
        var session = IlLines.Load(TaskPresenceComparisonExamples.Method(kind, originalPresent).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        Assert.IsEmpty(edit.Problems);
        session.CommitEdit(edit.Name, TaskPresenceComparisonExamples.Method(kind, editedPresent));
        Assert.IsNotNull(edit.Method);
        await AssertActualReturnAsync(edit.Original.Requested.Invoke(null, null), kind, originalPresent);
        await AssertActualReturnAsync(edit.OriginalMethod.Invoke(null, null), kind, originalPresent);
        await AssertActualReturnAsync(edit.Method.Invoke(null, null), kind, editedPresent);
        return session;
    }

    private static async Task AssertActualReturnAsync(object? value, int kind, bool present)
    {
        if (!present)
        {
            Assert.IsNull(value);
            return;
        }

        var task = Assert.IsInstanceOfType<Task>(value);
        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
        if (kind == 1)
        {
            Assert.AreEqual(42, await Assert.IsInstanceOfType<Task<int>>(value));
        }
        else if (kind == 2)
        {
            Assert.IsNull(await Assert.IsInstanceOfType<Task<string>>(value));
        }
    }

    private static void AssertSide(ComparisonSide side, int kind, bool present)
    {
        Assert.AreEqual("completed", side.Outcome, side.Detail);
        Assert.IsNull(side.Exception);
        Assert.IsNotNull(side.Result);
        Assert.AreEqual("", side.StandardOutput);
        Assert.AreEqual("", side.StandardError);
        var invocation = Assert.ContainsSingle(side.Invocations);
        Assert.IsNull(invocation.Exception);
        var input = Assert.ContainsSingle(invocation.Inputs);
        Assert.AreEqual("receiver", input.Name);
        Assert.AreEqual("null", input.Value.Kind);
        Assert.HasCount(2, invocation.Outputs);
        var returned = invocation.Outputs.Single(member => member.Name == "return").Value;
        AssertReturnedValue(returned, kind, present);
    }

    private static void AssertReturnedValue(ObservedValue value, int kind, bool present)
    {
        if (!present)
        {
            Assert.AreEqual(new ObservedValue("null-task", "System.Threading.Tasks.Task", null, null, []), value);
        }
        else if (kind == 1)
        {
            Assert.AreEqual("scalar", value.Kind);
            Assert.AreEqual("[System.Private.CoreLib]System.Int32", value.Type);
            Assert.AreEqual("42", value.Value);
            Assert.IsEmpty(value.Members);
        }
        else
        {
            Assert.AreEqual(new ObservedValue("null", "", null, null, []), value);
        }
    }

    private static void Add(Session session, string source)
    {
        foreach (var line in source.Split('\n')) session.AddLine(line);
    }

    private Task<ComparisonReply> RunAsync(Session session, string command) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, command), TestContext.CancellationToken);

    private static string Details(ComparisonReply reply) => reply.Original.Detail + "; " + reply.Edited.Detail;
}
