using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Exception details remain distinguishable through real comparison processes and structural graphs.
/// </summary>
[TestClass]
public sealed class ExceptionStateComparisonTests
{
    /// <summary>
    /// Supplies cancellation for comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Changing stored exception state changes the comparison while unchanged copies still match.
    /// </summary>
    /// <param name="kind">The stored state to change.</param>
    /// <param name="field">The field containing the changed state.</param>
    /// <param name="before">The original scalar value.</param>
    /// <param name="after">The edited scalar value.</param>
    [TestMethod]
    [DataRow("parameter", "_paramName", "left", "right")]
    [DataRow("actual", "_actualValue", "42", "43")]
    [DataRow("custom", "Detail", "left", "right")]
    [DataRow("data", "_data", "left", "right")]
    [DataRow("help", "_helpURL", "left", "right")]
    [DataRow("source", "_source", "left", "right")]
    public async Task Compare_StoredExceptionFieldsAffectOutcome(string kind, string field, string before, string after)
    {
        var session = IlLines.Load(ExceptionStateExamples.Source(kind, false).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var same = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", same.Outcome, same.Original.Detail + "; " + same.Edited.Detail);
        session.CommitEdit(edit.Name, ExceptionStateExamples.Method(kind, true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        var original = changed.Original.Invocations.Single().Exception!;
        var edited = changed.Edited.Invocations.Single().Exception!;
        Assert.AreEqual("bad", original.Message);
        Assert.AreEqual(original.Message, edited.Message);
        Assert.AreEqual(original.Type, edited.Type);
        Assert.AreEqual(original.HResult, edited.HResult);
        var originalField = original.Fields.Single(member => member.Name.EndsWith("::" + field, StringComparison.Ordinal)).Value;
        var editedField = edited.Fields.Single(member => member.Name.EndsWith("::" + field, StringComparison.Ordinal)).Value;
        Assert.Contains(before, Scalars(originalField));
        Assert.Contains(after, Scalars(editedField));
    }

    /// <summary>
    /// Exception fields retain references to the exception itself and to objects already present in the surrounding graph.
    /// </summary>
    [TestMethod]
    public void Observe_ExceptionFieldsPreserveCyclesAndSharedRoots()
    {
        var actual = new[] { 42 };
        var exception = new ArgumentOutOfRangeException("value", actual, "bad");
        exception.Data["self"] = exception;
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var argument = observer.Capture(actual);
        var observed = observer.Exception(exception);
        var value = observed.Fields.Single(member => member.Name.EndsWith("::_actualValue", StringComparison.Ordinal)).Value;
        Assert.AreEqual("reference", value.Kind);
        Assert.AreEqual(argument.Identity, value.Identity);
        var data = observed.Fields.Single(member => member.Name.EndsWith("::_data", StringComparison.Ordinal)).Value;
        Assert.Contains(member => member.Kind == "reference" && member.Identity == observed.Identity, Descendants(data));
        Assert.DoesNotContain(member => member.Kind == "unavailable", observed.Fields.SelectMany(member => Descendants(member.Value)));
    }

    /// <summary>
    /// Oversized custom state is explicitly unavailable instead of producing a complete match.
    /// </summary>
    [TestMethod]
    public async Task Compare_OversizedExceptionFieldIsIncomplete()
    {
        var source = ExceptionStateExamples.Method("actual", false).Replace("ldc.i4.s 42\nbox int32", "ldc.i4 5000\nnewarr int32",
            StringComparison.Ordinal);
        var session = IlLines.Load(source.Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("incomplete", result.Outcome);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.Contains(value => value.Kind == "unavailable",
                side.Invocations.Single().Exception!.Fields.SelectMany(member => Descendants(member.Value)));
        }
    }

    /// <summary>
    /// Real thrown exceptions keep custom fields without reading virtual getters or runtime dispatch data.
    /// </summary>
    [TestMethod]
    public void Observe_CustomExceptionNeverInvokesFormatting()
    {
        var caught = Assert.ThrowsExactly<StoredFieldException>(() => throw new StoredFieldException("detail"));
        var observer = new StructuralObservation(new Dictionary<string, string>());
        var observed = observer.Exception(caught);
        Assert.AreEqual("stored message", observed.Message);
        var detail = observed.Fields.Single(member => member.Name.EndsWith("::Detail", StringComparison.Ordinal));
        Assert.AreEqual("detail", detail.Value.Value);
        Assert.DoesNotContain(member => member.Value.Kind == "unavailable", observed.Fields);
        var returned = new StructuralObservation(new Dictionary<string, string>()).Capture(caught);
        Assert.DoesNotContain(member => member.Kind == "unavailable", Descendants(returned));
        Assert.Contains("stored message", Scalars(returned));
        Assert.Contains("detail", Scalars(returned));
    }

    /// <summary>
    /// Null-filled exception data still consumes the observation budget and cannot bypass its size limit.
    /// </summary>
    [TestMethod]
    public void Observe_NullFilledExceptionFieldIsBounded()
    {
        var observed = new StructuralObservation(new Dictionary<string, string>())
            .Exception(new ArgumentOutOfRangeException("value", new object?[5000], "bad"));
        var actual = observed.Fields.Single(member => member.Name.EndsWith("::_actualValue", StringComparison.Ordinal)).Value;
        Assert.IsLessThan(5000, actual.Members.Count);
        Assert.AreEqual("unavailable", actual.Members[^1].Value.Kind);
    }

    private static IEnumerable<ObservedValue> Descendants(ObservedValue value) =>
        value.Members.SelectMany(member => Descendants(member.Value)).Prepend(value);

    private static IEnumerable<string?> Scalars(ObservedValue value) => Descendants(value)
        .Where(member => member.Kind == "scalar").Select(member => member.Value);
}
