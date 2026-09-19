using System.Reflection;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Aggregate failures preserve every child through real comparison workers and bounded structural observation.
/// </summary>
[TestClass]
public sealed class AggregateExceptionComparisonTests
{
    /// <summary>
    /// Supplies cancellation for the comparison worker processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A later aggregate child changes the result even when the outer message and first child are identical.
    /// </summary>
    /// <param name="nested">Whether the differing child belongs to a nested aggregate.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Compare_LaterAggregateChildIsObserved(bool nested)
    {
        var session = IlLines.Load(AggregateExceptionExamples.Method(false, nested).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var same = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", same.Outcome, same.Original.Detail + "; " + same.Edited.Detail);
        session.CommitEdit(edit.Name, AggregateExceptionExamples.Method(true, nested));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        var original = changed.Original.Invocations.Single().Exception!;
        var edited = changed.Edited.Invocations.Single().Exception!;
        Assert.AreEqual("outer failure", original.Message);
        Assert.AreEqual(original.Message, edited.Message);
        Assert.AreEqual("first failure", original.Inner!.Message);
        Assert.AreEqual(original.Inner.Message, edited.Inner!.Message);
        Assert.HasCount(1, original.AdditionalInnerExceptions);
        Assert.HasCount(1, edited.AdditionalInnerExceptions);
        var before = original.AdditionalInnerExceptions[0];
        var after = edited.AdditionalInnerExceptions[0];
        if (nested)
        {
            Assert.AreEqual("nested failure", before.Message);
            Assert.AreEqual(before.Message, after.Message);
            Assert.AreEqual("nested first failure", before.Inner!.Message);
            Assert.AreEqual(before.Inner.Message, after.Inner!.Message);
            before = before.AdditionalInnerExceptions.Single();
            after = after.AdditionalInnerExceptions.Single();
        }

        Assert.AreEqual("original detail", before.Message);
        Assert.AreEqual("edited detail", after.Message);
    }

    /// <summary>
    /// Repeated sibling references are captured independently and are not mistaken for an exception cycle.
    /// </summary>
    [TestMethod]
    public void Observe_SharedAggregateChildIsComplete()
    {
        var leaf = new InvalidOperationException("shared detail");
        var aggregate = new AggregateException("outer failure", leaf, leaf);
        var observed = new StructuralObservation(new Dictionary<string, string>()).Exception(aggregate);
        Assert.IsNull(observed.Problem);
        Assert.AreEqual("shared detail", observed.Inner!.Message);
        Assert.IsNull(observed.Inner.Problem);
        Assert.AreEqual("shared detail", observed.AdditionalInnerExceptions.Single().Message);
        Assert.IsNull(observed.AdditionalInnerExceptions[0].Problem);
        var repeated = new StructuralObservation(new Dictionary<string, string>()).Exception(aggregate);
        Assert.AreEqual(observed, repeated);
        Assert.AreEqual(observed.GetHashCode(), repeated.GetHashCode());
    }

    /// <summary>
    /// A cycle in a later child terminates with a problem while preserving the first sibling's message.
    /// </summary>
    [TestMethod]
    public void Observe_CycleInLaterChildReportsProblem()
    {
        var leaf = new Exception("later failure");
        var aggregate = new AggregateException("outer failure", new Exception("first failure"), leaf);
        typeof(Exception).GetField("_innerException", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(leaf, aggregate);
        var observed = new StructuralObservation(new Dictionary<string, string>()).Exception(aggregate);
        Assert.AreEqual("first failure", observed.Inner!.Message);
        var later = observed.AdditionalInnerExceptions.Single();
        Assert.AreEqual("later failure", later.Message);
        Assert.AreEqual("exception chain is cyclic or exceeds the observation depth limit", later.Inner!.Problem);
    }

    /// <summary>
    /// Wide aggregate trees retain their captured prefix and explicitly report the bounded observation.
    /// </summary>
    [TestMethod]
    public void Observe_WideAggregateReportsLimit()
    {
        var aggregate = new AggregateException("outer failure", Enumerable.Range(0, 5000).Select(index => new Exception(index.ToString())));
        var observed = new StructuralObservation(new Dictionary<string, string>()).Exception(aggregate);
        Assert.AreEqual("0", observed.Inner!.Message);
        Assert.AreEqual("1", observed.AdditionalInnerExceptions[0].Message);
        Assert.IsLessThan(5000, observed.AdditionalInnerExceptions.Count);
        Assert.AreEqual("exception tree exceeds the observation node limit", observed.AdditionalInnerExceptions[^1].Problem);
    }

    /// <summary>
    /// Truncation inside a later aggregate child makes the complete comparison unavailable even when both sides agree.
    /// </summary>
    [TestMethod]
    public async Task Compare_DeepLaterChildIsIncomplete()
    {
        var session = IlLines.Load(AggregateExceptionExamples.DeepMethod().Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("incomplete", result.Outcome);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            var exception = side.Invocations.Single().Exception!;
            Assert.AreEqual("outer failure", exception.Message);
            Assert.AreEqual("first failure", exception.Inner!.Message);
            var last = exception.AdditionalInnerExceptions.Single();
            while (last.Inner is { } inner)
            {
                last = inner;
            }

            Assert.AreEqual("exception chain is cyclic or exceeds the observation depth limit", last.Problem);
        }
    }
}
