using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Scalar object graphs preserve reference identity while retaining the exact scalar value on every occurrence.
/// </summary>
[TestClass]
public sealed class ScalarAliasComparisonTests
{
    private static readonly string[] ExpectedValues = ["x", "x", "42", "42"];
    private static readonly string[] ExpectedIndices = ["0", "1", "2", "3"];

    /// <summary>
    /// Supplies cancellation to the actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Equal strings and boxed integers differ when an edited scenario returns distinct objects instead of shared references.
    /// </summary>
    /// <param name="splitStrings">Whether the edited result creates separate equal strings.</param>
    /// <param name="splitBoxes">Whether the edited result creates separate equal boxed integers.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Scenario_SharedAndDistinctScalarReferencesPreserveValuesAndIdentity(bool splitStrings, bool splitBoxes)
    {
        var session = IlLines.Load(".method object[] Pair() {", ".locals init (object[] values, string text, object number)",
            "ldc.i4.4", "newarr object", "stloc.0", "ldc.i4.s 120", "ldc.i4.1",
            "newobj instance void String::.ctor(char, int32)", "stloc.1", "ldc.i4.s 42", "box int32", "stloc.2",
            "ldloc.0", "ldc.i4.0", "ldloc.1", "stelem.ref", "ldloc.0", "ldc.i4.1", "ldloc.1", "stelem.ref",
            "ldloc.0", "ldc.i4.2", "ldloc.2", "stelem.ref", "ldloc.0", "ldc.i4.3", "ldloc.2", "stelem.ref",
            "ldloc.0", "ret", "}");
        var edit = session.PrepareEdit("Pair", "Copy");
        var source = edit.Source;
        if (splitStrings)
        {
            source = source.Replace("ldloc.1", "ldc.i4.s 120\nldc.i4.1\nnewobj instance void String::.ctor(char, int32)",
                StringComparison.Ordinal);
        }

        if (splitBoxes)
        {
            source = source.Replace("ldloc.2", "ldc.i4.s 42\nbox int32", StringComparison.Ordinal);
        }

        session.CommitEdit(edit.Name, source);
        foreach (var line in IlLines.Expand(".method object[] Scenario() { call object[] Copy(); ret }"))
        {
            session.AddLine(line);
        }

        var reply = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual(splitStrings || splitBoxes ? "different" : "match", reply.Outcome,
            reply.Original.Detail + "\n" + reply.Edited.Detail);
        AssertGraph(reply.Original.Result!, false, false);
        AssertGraph(reply.Edited.Result!, splitStrings, splitBoxes);
        Assert.HasCount(1, reply.Original.Invocations);
        Assert.HasCount(1, reply.Edited.Invocations);
        AssertGraph(reply.Original.Invocations[0].Outputs.Single(member => member.Name == "return").Value, false, false);
        AssertGraph(reply.Edited.Invocations[0].Outputs.Single(member => member.Name == "return").Value, splitStrings, splitBoxes);
    }

    /// <summary>
    /// A shared custom boxed struct retains its reference edge independently of equal field values in separate boxes.
    /// </summary>
    /// <param name="splitBoxes">Whether the edited result boxes the same struct separately for each array element.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scenario_CustomBoxedStructSharingPreservesReferenceEdges(bool splitBoxes)
    {
        var session = IlLines.Load(".class public sequential sealed Payload extends System.ValueType {", ".field public int32 Number", "}",
            ".method object[] Pair() {", ".locals init (object[] values, object boxed, valuetype Payload value)",
            "ldloca.s 2", "ldc.i4.s 42", "stfld int32 Payload::Number", "ldloc.2", "box Payload", "stloc.1",
            "ldc.i4.2", "newarr object", "stloc.0", "ldloc.0", "ldc.i4.0", "ldloc.1", "stelem.ref",
            "ldloc.0", "ldc.i4.1", "ldloc.1", "stelem.ref", "ldloc.0", "ret", "}");
        var edit = session.PrepareEdit("Pair", "Copy");
        var source = splitBoxes ? edit.Source.Replace("ldloc.1", "ldloc.2\nbox Payload", StringComparison.Ordinal) : edit.Source;
        session.CommitEdit(edit.Name, source);
        foreach (var line in IlLines.Expand(".method object[] Scenario() { call object[] Copy(); ret }"))
        {
            session.AddLine(line);
        }

        var reply = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual(splitBoxes ? "different" : "match", reply.Outcome, reply.Original.Detail + "\n" + reply.Edited.Detail);
        AssertBoxedStructGraph(reply.Original.Result!, false);
        AssertBoxedStructGraph(reply.Edited.Result!, splitBoxes);
        Assert.HasCount(1, reply.Original.Invocations);
        Assert.HasCount(1, reply.Edited.Invocations);
        AssertBoxedStructGraph(reply.Original.Invocations[0].Outputs.Single(member => member.Name == "return").Value, false);
        AssertBoxedStructGraph(reply.Edited.Invocations[0].Outputs.Single(member => member.Name == "return").Value, splitBoxes);
    }

    private static void AssertBoxedStructGraph(ObservedValue graph, bool splitBoxes)
    {
        Assert.AreEqual("array", graph.Kind);
        Assert.AreEqual("0:2", graph.Value);
        Assert.AreEqual(1, graph.Identity);
        Assert.HasCount(2, graph.Members);
        var first = graph.Members[0].Value;
        var second = graph.Members[1].Value;
        Assert.AreEqual("object", first.Kind);
        Assert.AreEqual(2, first.Identity);
        Assert.HasCount(1, first.Members);
        Assert.EndsWith("::Number", first.Members[0].Name);
        Assert.AreEqual("42", first.Members[0].Value.Value);
        Assert.AreEqual(3, first.Members[0].Value.Identity);
        Assert.AreEqual(first.Type, second.Type);
        if (splitBoxes)
        {
            Assert.AreEqual("object", second.Kind);
            Assert.AreEqual(4, second.Identity);
            Assert.HasCount(1, second.Members);
            Assert.AreEqual(first.Members[0].Name, second.Members[0].Name);
            Assert.AreEqual("42", second.Members[0].Value.Value);
            Assert.AreEqual(5, second.Members[0].Value.Identity);
        }
        else
        {
            Assert.AreEqual("reference", second.Kind);
            Assert.AreEqual(first.Identity, second.Identity);
            Assert.IsNull(second.Value);
            Assert.IsEmpty(second.Members);
        }
    }

    private static void AssertGraph(ObservedValue graph, bool splitStrings, bool splitBoxes)
    {
        Assert.AreEqual("array", graph.Kind);
        Assert.AreEqual("0:4", graph.Value);
        Assert.AreEqual(1, graph.Identity);
        Assert.HasCount(4, graph.Members);
        Assert.AreSequenceEqual(ExpectedValues, graph.Members.Select(member => member.Value.Value));
        Assert.AreSequenceEqual(ExpectedIndices, graph.Members.Select(member => member.Name));
        var boxIdentity = splitStrings ? 4 : 3;
        var identities = new[] { 2, splitStrings ? 3 : 2, boxIdentity, splitBoxes ? boxIdentity + 1 : boxIdentity };
        Assert.AreSequenceEqual(identities.Select(identity => (int?)identity), graph.Members.Select(member => member.Value.Identity));
        foreach (var member in graph.Members)
        {
            Assert.AreEqual("scalar", member.Value.Kind);
            Assert.IsEmpty(member.Value.Members);
        }

        Assert.EndsWith("System.String", graph.Members[0].Value.Type);
        Assert.EndsWith("System.String", graph.Members[1].Value.Type);
        Assert.EndsWith("System.Int32", graph.Members[2].Value.Type);
        Assert.EndsWith("System.Int32", graph.Members[3].Value.Type);
    }
}
