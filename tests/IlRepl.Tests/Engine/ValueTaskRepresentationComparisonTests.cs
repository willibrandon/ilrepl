using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Value-task comparisons preserve task identity and leave single-consumption sources under the caller's control.
/// </summary>
[TestClass]
public sealed class ValueTaskRepresentationComparisonTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A scenario distinguishes a task-backed value task from the same result represented inline.
    /// </summary>
    /// <param name="generic">Whether the selected value task has a result.</param>
    /// <returns>The completed representation and execution assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scenario_ValueTaskRepresentation_RemainsDistinct(bool generic)
    {
        var session = IlLines.Load(ValueTaskComparisonExamples.Source(generic, false).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ValueTaskComparisonExamples.Source(generic, true));
        foreach (var line in ValueTaskComparisonExamples.Scenario(generic).Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.AreEqual(generic ? "true" : "false", result.Original.Result!.Value);
        Assert.AreEqual(generic ? "false" : "true", result.Edited.Result!.Value);
        Assert.IsNull(result.Original.Exception);
        Assert.IsNull(result.Edited.Exception);
    }

    /// <summary>
    /// A pooled channel read is consumed once by the scenario or by the worker when returned directly.
    /// </summary>
    /// <param name="returnSource">Whether the worker receives the original value task to await.</param>
    /// <returns>The completed consumption and observation assertions.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scenario_SourceBackedValueTask_IsNotConsumedByInstrumentation(bool returnSource)
    {
        const string valueTask = "valuetype System.Threading.Tasks.ValueTask`1<int32>";
        var session = new Session();
        session.Resolver.Load(typeof(ComparisonAsyncSource).Assembly.Location);
        foreach (var line in IlLines.Expand(".method " + valueTask + " Read() { ldc.i4.0; call " + valueTask
            + " [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonAsyncSource::Start(bool); ret }"))
        {
            session.AddLine(line);
        }

        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var scenario = returnSource ? ".method " + valueTask + " Scenario() {\ncall Copy\nret\n}"
            : ".method int32 Scenario() {\n.locals init (" + valueTask + " value)\ncall Copy\nstloc.0\nldloca 0\n"
                + "call instance !0 " + valueTask + "::get_Result()\nret\n}";
        foreach (var line in scenario.Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual(returnSource ? "match" : "incomplete", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual("42", side.Result!.Value);
            var returned = side.Invocations.Single().Outputs.Single(member => member.Name == "return").Value;
            Assert.AreEqual(returnSource ? "scalar" : "unavailable", returned.Kind);
        }
    }

    /// <summary>
    /// An async iterator's non-generic disposal value task retains its original source and completion.
    /// </summary>
    /// <returns>The completed source identity and worker assertions.</returns>
    [TestMethod]
    public async Task Scenario_IteratorDisposal_PreservesItsOriginalSource()
    {
        const string valueTask = "valuetype System.Threading.Tasks.ValueTask";
        const string source = "[IlRepl.Tests]IlRepl.Tests.Engine.ComparisonValueTaskSource";
        var session = new Session();
        session.Resolver.Load(typeof(ComparisonValueTaskSource).Assembly.Location);
        foreach (var line in IlLines.Expand(".method " + valueTask + " Read() { call " + valueTask + " " + source + "::Start(); ret }"))
        {
            session.AddLine(line);
        }

        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var scenario = ".method " + valueTask + " Scenario() {\n.locals init (" + valueTask + " value)\ncall Copy\nstloc.0\n"
            + "ldloca 0\ncall " + valueTask + " " + source + "::get_Last()\ncall instance bool " + valueTask + "::Equals(" + valueTask
            + ")\nbrtrue SAME\nldstr \"value task was replaced\"\nnewobj instance void InvalidOperationException::.ctor(string)\n"
            + "throw\nSAME: ldloc.0\nret\n}";
        foreach (var line in scenario.Split('\n'))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        Assert.IsNull(result.Original.Exception);
        Assert.IsNull(result.Edited.Exception);
        Assert.AreEqual("null", result.Original.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Kind);
        Assert.AreEqual("null", result.Edited.Invocations.Single().Outputs.Single(member => member.Name == "return").Value.Kind);
    }
}
