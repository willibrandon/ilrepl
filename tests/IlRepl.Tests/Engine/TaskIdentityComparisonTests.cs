using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Real comparison workers preserve returned task identity and state while observing completion.
/// </summary>
[TestClass]
public sealed class TaskIdentityComparisonTests
{
    private const string Source = "[IlRepl.Tests]IlRepl.Tests.Engine.ComparisonTaskSource";
    private const string IntTask = "class System.Threading.Tasks.Task`1<int32>";

    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A scenario distinguishes a cached task from distinct tasks carrying the same result.
    /// </summary>
    /// <param name="generic">Whether the selected method declares the task's result type.</param>
    /// <returns>The completed identity and invocation assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scenario_TaskIdentity_DistinguishesCachedAndFreshTasks(bool generic)
    {
        var session = CreateSession(generic, "Cached");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("::Cached()", "::Fresh()", StringComparison.Ordinal));
        Add(session, ".method bool Scenario() {", "call Copy", "call Copy",
            "call bool Object::ReferenceEquals(object, object)", "ret", "}");

        var result = await Run(session);

        Assert.AreEqual("different", result.Outcome, Details(result));
        Assert.AreEqual("true", result.Original.Result!.Value);
        Assert.AreEqual("false", result.Edited.Result!.Value);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.HasCount(2, side.Invocations);
            foreach (var invocation in side.Invocations)
            {
                Assert.IsNull(invocation.Exception);
                var returned = invocation.Outputs.Single(member => member.Name == "return").Value;
                Assert.AreEqual(generic ? "42" : null, returned.Value);
                Assert.AreEqual(generic ? "scalar" : "null", returned.Kind);
            }
        }
    }

    /// <summary>
    /// Task metadata remains visible to the scenario instead of being replaced by tracker metadata.
    /// </summary>
    /// <param name="generic">Whether the selected method declares the task's result type.</param>
    /// <returns>The completed state assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scenario_TaskAsyncState_PreservesTheReturnedObject(bool generic)
    {
        var session = new Session();
        session.Resolver.Load(typeof(ComparisonTaskSource).Assembly.Location);
        Add(session, ".method " + TaskType(generic) + " Read() {", "ldstr \"before\"",
            "call " + IntTask + " " + Source + "::WithState(string)", "ret", "}");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("\"before\"", "\"after\"", StringComparison.Ordinal));
        Add(session, ".method string Scenario() {", "call Copy",
            "callvirt instance object System.Threading.Tasks.Task::get_AsyncState()", "castclass string", "ret", "}");

        var result = await Run(session);

        Assert.AreEqual("different", result.Outcome, Details(result));
        Assert.AreEqual("before", result.Original.Result!.Value);
        Assert.AreEqual("after", result.Edited.Result!.Value);
        Assert.AreEqual("completed", result.Original.Outcome);
        Assert.AreEqual("completed", result.Edited.Outcome);
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
    }

    /// <summary>
    /// Awaited tasks retain identity and their observations finish even when continuations are queued asynchronously.
    /// </summary>
    /// <param name="generic">Whether the selected method declares the task's result type.</param>
    /// <param name="outcome">Zero succeeds, one faults, and two cancels the task.</param>
    /// <returns>The completed task and invocation assertions.</returns>
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(false, 1)]
    [DataRow(true, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 2)]
    public async Task Scenario_PendingTask_RetainsIdentityAndCompletion(bool generic, int outcome)
    {
        var session = CreateSession(generic, "Pending");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        Add(session, ".method " + TaskType(generic) + " Scenario() {", ".locals init (" + TaskType(generic) + " pending)",
            "call Copy", "stloc.0", "ldloc.0", "call " + IntTask + " " + Source + "::Pending()",
            "call bool Object::ReferenceEquals(object, object)", "brtrue.s SAME", "ldstr \"task identity changed\"",
            "newobj instance void InvalidOperationException::.ctor(string)", "throw", "SAME: ldc.i4 " + outcome,
            "call void " + Source + "::Complete(int32)", "ldloc.0", "ret", "}");

        var result = await Run(session);

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.HasCount(1, side.Invocations);
            var invocation = side.Invocations[0];
            if (outcome == 0)
            {
                Assert.IsNull(side.Exception);
                Assert.IsNull(invocation.Exception);
                Assert.AreEqual("42", side.Result!.Value);
                Assert.AreEqual(generic ? "42" : null, invocation.Outputs.Single(member => member.Name == "return").Value.Value);
            }
            else
            {
                Assert.IsNotNull(side.Exception);
                Assert.EndsWith(outcome == 1 ? "InvalidOperationException" : "TaskCanceledException", side.Exception.Type);
                Assert.AreEqual(side.Exception, invocation.Exception);
                if (outcome == 1)
                {
                    Assert.AreEqual("task failure", side.Exception.Message);
                }
            }
        }
    }

    /// <summary>
    /// A scenario that leaves its selected task running cannot produce a complete comparison.
    /// </summary>
    /// <param name="generic">Whether the selected method declares the task's result type.</param>
    /// <returns>The completed incomplete-result assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scenario_UnfinishedTask_RemainsIncomplete(bool generic)
    {
        var session = CreateSession(generic, "Pending");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        Add(session, ".method int32 Scenario() {", "call Copy", "pop", "ldc.i4.s 42", "ret", "}");

        var result = await Run(session);

        Assert.AreEqual("incomplete", result.Outcome);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("setup-failed", side.Outcome);
            Assert.AreEqual("a selected-method invocation was still running when its scenario completed", side.Detail);
            Assert.IsEmpty(side.Invocations);
        }
    }

    private static Session CreateSession(bool generic, string method)
    {
        var session = new Session();
        session.Resolver.Load(typeof(ComparisonTaskSource).Assembly.Location);
        Add(session, ".method " + TaskType(generic) + " Read() {", "call " + IntTask + " " + Source + "::" + method + "()",
            "ret", "}");
        return session;
    }

    private static string TaskType(bool generic) => generic ? IntTask : "class System.Threading.Tasks.Task";

    private static void Add(Session session, params string[] lines)
    {
        foreach (var line in IlLines.Expand(lines))
        {
            session.AddLine(line);
        }
    }

    private Task<ComparisonReply> Run(Session session) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"), TestContext.CancellationToken);

    private static string Details(ComparisonReply result) => result.Original.Detail + "; " + result.Edited.Detail;
}
