using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// LINQ lookup observations preserve logical group contents across independent runtimes.
/// </summary>
[TestClass]
public sealed class LookupComparisonTests
{
    /// <summary>
    /// Supplies cancellation for real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Separate workers match equal lookups and detect an edited group value without comparing randomized hash storage.
    /// </summary>
    [TestMethod]
    public async Task Compare_LookupContentsIgnoreRandomizedStorage()
    {
        var session = new Session();
        foreach (var line in LookupComparisonExamples.KeyMethod.Split('\n'))
        {
            session.AddLine(line);
        }

        foreach (var line in LookupComparisonExamples.Method(edited: false).Split('\n'))
        {
            session.AddLine(line);
        }

        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        var same = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("match", same.Outcome, same.Original.Detail + "; " + same.Edited.Detail);
        foreach (var lookup in new[] { same.Original, same.Edited }.Select(side => side.Result!))
        {
            Assert.AreEqual("lookup", lookup.Kind);
            Assert.HasCount(3, lookup.Members);
            Assert.AreEqual("comparer", lookup.Members[0].Name);
            Assert.AreEqual("first", lookup.Members[1].Value.Members[0].Value.Value);
            Assert.AreSequenceEqual(["42", "44"], lookup.Members[1].Value.Members.Skip(1).Select(member => member.Value.Value));
            Assert.AreEqual("second", lookup.Members[2].Value.Members[0].Value.Value);
            Assert.AreEqual("43", lookup.Members[2].Value.Members[1].Value.Value);
        }

        session.CommitEdit(edit.Name, LookupComparisonExamples.Method(edited: true));
        var changed = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", changed.Outcome, changed.Original.Detail + "; " + changed.Edited.Detail);
        Assert.AreEqual("43", changed.Original.Result!.Members[2].Value.Members[1].Value.Value);
        Assert.AreEqual("45", changed.Edited.Result!.Members[2].Value.Members[1].Value.Value);
    }

    /// <summary>
    /// Lookup comparer settings remain observable even when group contents are identical.
    /// </summary>
    [TestMethod]
    public void Capture_LookupPreservesComparer()
    {
        var values = new[] { "first", "second" };
        var ordinal = values.ToLookup(value => value, StringComparer.Ordinal);
        var ignoreCase = values.ToLookup(value => value, StringComparer.OrdinalIgnoreCase);
        Assert.AreNotEqual(Observe(ordinal), Observe(ignoreCase));
    }

    /// <summary>
    /// User lookup implementations become unavailable without invoking their getters, searches, or enumerators.
    /// </summary>
    [TestMethod]
    public void Capture_UserLookupDoesNotInvokeUserCode()
    {
        var calls = 0;
        var observed = Observe(new UserLookupObservationProbe(() => calls++));
        Assert.AreEqual("unavailable", observed.Kind);
        Assert.Contains("without invoking user code", observed.Value!);
        Assert.AreEqual(0, calls);
    }

    private static ObservedValue Observe(object value) =>
        new StructuralObservation(new Dictionary<string, string>()).Capture(value);
}
