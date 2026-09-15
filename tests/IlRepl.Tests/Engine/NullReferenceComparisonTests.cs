using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Observation preserves null managed references without reading through them before or after the selected call.
/// </summary>
[TestClass]
public sealed class NullReferenceComparisonTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A method can inspect a null reference without the wrapper dereferencing its parameter first.
    /// </summary>
    /// <param name="type">The referenced value type.</param>
    /// <returns>The completed null-reference and execution assertions.</returns>
    [TestMethod]
    [DataRow("int32")]
    [DataRow("object")]
    [DataRow("valuetype DateTime")]
    [DataRow("int32 modopt(System.Runtime.CompilerServices.IsLong)")]
    public async Task Run_NullReferenceArgument_ExecutesTheSelectedMethod(string type)
    {
        var session = IlLines.Load((".method int32 Read(" + type + "& value) {\nldarg.0\nldc.i4.0\nconv.i\nceq\nret\n}")
            .Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ret", "ldc.i4.1\nadd\nret", StringComparison.Ordinal));
        foreach (var line in IlLines.Expand(".method int32 Scenario() {", "ldc.i4.0", "conv.i", "call Copy", "ret", "}"))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.IsNull(side.Exception);
            Assert.AreEqual(side == result.Original ? "1" : "2", side.Result!.Value);
            var call = side.Invocations.Single();
            Assert.AreEqual("null-reference", call.Inputs.Single(member => member.Name == "argument 0").Value.Kind);
            Assert.AreEqual("null-reference", call.Outputs.Single(member => member.Name == "argument 0").Value.Kind);
        }
    }

    /// <summary>
    /// A returned null managed reference remains null and is distinct from a reference whose stored value is null.
    /// </summary>
    /// <returns>The completed argument and return identity assertions.</returns>
    [TestMethod]
    public async Task Run_NullReferenceReturn_DistinguishesNullStoredValues()
    {
        var session = IlLines.Load(".method object& Read(object& value) { ldarg.0; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        foreach (var line in IlLines.Expand(".method int32 Scenario() {", ".locals init (object value, int32 result)",
            "ldc.i4.0", "conv.i", "call Copy", "ldc.i4.0", "conv.i", "ceq", "stloc.1",
            "ldloca 0", "call Copy", "ldind.ref", "ldnull", "ceq", "ldloc.1", "add", "ret", "}"))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("2", side.Result!.Value);
            Assert.HasCount(2, side.Invocations);
            for (var index = 0; index < side.Invocations.Count; index++)
            {
                var call = side.Invocations[index];
                var expected = index == 0 ? "null-reference" : "null";
                Assert.AreEqual(expected, call.Inputs.Single(member => member.Name == "argument 0").Value.Kind);
                Assert.AreEqual(expected, call.Outputs.Single(member => member.Name == "return").Value.Kind);
            }
        }
    }

    /// <summary>
    /// A value-type instance method can inspect a null receiver without an injected load from that address.
    /// </summary>
    /// <returns>The completed receiver and scalar result assertions.</returns>
    [TestMethod]
    public async Task Run_NullValueTypeReceiver_DoesNotDereferenceThis()
    {
        var session = IlLines.Load(".class public sequential sealed NullReferenceReceiver extends ValueType {",
            ".method public instance int32 Read() { ldarg.0; ldc.i4.0; conv.i; ceq; ret }", "}");
        var edit = session.PrepareEdit("instance int32 NullReferenceReceiver::Read()", "Copy");
        session.CommitEdit(edit.Name, edit.Source);
        foreach (var line in IlLines.Expand(".method int32 Scenario() {", "ldc.i4.0", "conv.i", "call Copy", "ret", "}"))
        {
            session.AddLine(line);
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy using Scenario"),
            TestContext.CancellationToken);

        Assert.AreEqual("match", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("1", side.Result!.Value);
            var call = side.Invocations.Single();
            Assert.AreEqual("null-reference", call.Inputs.Single(member => member.Name == "receiver").Value.Kind);
            Assert.AreEqual("null-reference", call.Outputs.Single(member => member.Name == "receiver").Value.Kind);
        }
    }
}
