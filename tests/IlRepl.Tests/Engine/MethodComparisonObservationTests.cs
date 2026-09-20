using System.Globalization;
using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Protocol;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Structural observations preserve exact values and graph identity without invoking user formatting or equality.
/// </summary>
[TestClass]
public sealed class MethodComparisonObservationTests
{
    private static readonly string[] ExpectedArrayIndices = ["0", "1", "2", "3"];
    private static readonly string[] ExpectedArrayValues = ["10", "20", "30", "40"];

    /// <summary>
    /// The cancellation token for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Floating-point observations distinguish signed zero and NaN payloads across real independently executed copies.
    /// </summary>
    /// <param name="beforeBits">The original IEEE754 bits.</param>
    /// <param name="afterBits">The edited IEEE754 bits.</param>
    [TestMethod]
    [DataRow("0000000000000000", "8000000000000000")]
    [DataRow("7ff8000000000001", "7ff8000000000002")]
    public async Task Run_FloatingPointBitsRemainExact(string beforeBits, string afterBits)
    {
        var session = IlLines.Load($".method float64 Value() {{ ldc.r8 float64(0x{beforeBits}); ret }}");
        var edit = session.PrepareEdit("Value", "Copy");
        var constant = edit.Original.Entries.Single(entry => entry.Instruction?.Op.Name == "ldc.r8").Instruction!;
        session.CommitEdit(edit.Name, edit.Source.Replace(constant.Text, $"ldc.r8 float64(0x{afterBits})", StringComparison.Ordinal));

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("different", result.Outcome, Details(result));
        Assert.AreEqual("scalar", result.Original.Result!.Kind);
        Assert.EndsWith("System.Double", result.Original.Result.Type);
        Assert.AreEqual(beforeBits, result.Original.Result.Value);
        Assert.AreEqual(afterBits, result.Edited.Result!.Value);
    }

    /// <summary>
    /// Shared objects and distinct objects with identical fields produce different observed return graphs.
    /// </summary>
    [TestMethod]
    public async Task Run_ReturnedAliasesAreDifferentFromDistinctObjects()
    {
        var session = IlLines.Load(".method object[] Pair() {", ".locals init (object[] pair, object value)",
            "ldc.i4.2", "newarr object", "stloc.0", "newobj instance void Object::.ctor()", "stloc.1",
            "ldloc.0", "ldc.i4.0", "ldloc.1", "stelem.ref", "ldloc.0", "ldc.i4.1", "ldloc.1", "stelem.ref",
            "ldloc.0", "ret", "}");
        var edit = session.PrepareEdit("Pair", "Copy");
        session.CommitEdit(edit.Name, edit.Source.Replace("ldloc.1", "newobj instance void Object::.ctor()", StringComparison.Ordinal));

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("different", result.Outcome, Details(result));
        var original = result.Original.Result!;
        var changed = result.Edited.Result!;
        Assert.AreEqual("array", original.Kind);
        Assert.AreEqual("0:2", original.Value);
        Assert.HasCount(2, original.Members);
        Assert.AreEqual("object", original.Members[0].Value.Kind);
        Assert.AreEqual("reference", original.Members[1].Value.Kind);
        Assert.AreEqual(original.Members[0].Value.Identity, original.Members[1].Value.Identity);
        Assert.AreEqual("object", changed.Members[0].Value.Kind);
        Assert.AreEqual("object", changed.Members[1].Value.Kind);
        Assert.AreNotEqual(changed.Members[0].Value.Identity, changed.Members[1].Value.Identity);
    }

    /// <summary>
    /// A returned array containing itself terminates as a reference edge and compares equal in independent workers.
    /// </summary>
    [TestMethod]
    public async Task Run_ReturnedCyclesAreBoundedAndPreserveIdentity()
    {
        var session = IlLines.Load(".method object[] Cycle() {", ".locals init (object[] values)", "ldc.i4.1",
            "newarr object", "stloc.0", "ldloc.0", "ldc.i4.0", "ldloc.0", "stelem.ref", "ldloc.0", "ret", "}");
        var edit = session.PrepareEdit("Cycle", "Copy");
        session.CommitEdit(edit.Name, edit.Source);

        var result = await Run(session, "Copy ()");

        Assert.AreEqual("match", result.Outcome, Details(result));
        foreach (var value in new[] { result.Original, result.Edited }.Select(side => side.Result!))
        {
            Assert.AreEqual("array", value.Kind);
            Assert.HasCount(1, value.Members);
            Assert.AreEqual("reference", value.Members[0].Value.Kind);
            Assert.AreEqual(value.Identity, value.Members[0].Value.Identity);
            Assert.IsEmpty(value.Members[0].Value.Members);
        }
    }

    /// <summary>
    /// Reading real private fields and cyclic aliases invokes no computed property, equality, hash, or formatting code.
    /// </summary>
    [TestMethod]
    public void Capture_PrivateFieldsAndAliasesDoNotInvokeUserCode()
    {
        var child = new ComparisonObservedNode { Name = "child" };
        var root = new ComparisonObservedNode { Name = "root", First = child, Second = child };
        child.First = root;
        var observer = new StructuralObservation(new Dictionary<string, string>());

        var value = observer.Capture(root);

        Assert.AreEqual("object", value.Kind);
        var first = Field(value, "First");
        var second = Field(value, "Second");
        Assert.AreEqual("object", first.Kind);
        Assert.AreEqual("reference", second.Kind);
        Assert.AreEqual(first.Identity, second.Identity);
        Assert.AreEqual("reference", Field(first, "First").Kind);
        Assert.AreEqual(value.Identity, Field(first, "First").Identity);
        Assert.AreEqual("42", Field(value, "_secret").Value);
        Assert.AreEqual("root", Field(value, "Name").Value);
        Assert.DoesNotContain(member => member.Name.EndsWith("::Secret", StringComparison.Ordinal), value.Members);
        Assert.AreEqual(0, root.UserCodeCalls);
        Assert.AreEqual(0, child.UserCodeCalls);
    }

    /// <summary>
    /// Multidimensional arrays preserve nonzero lower bounds and deterministic element order.
    /// </summary>
    [TestMethod]
    public void Capture_ArrayBoundsAndRowMajorValuesRemainExact()
    {
        var array = Array.CreateInstance(typeof(int), [2, 2], [-1, 3]);
        array.SetValue(10, -1, 3);
        array.SetValue(20, -1, 4);
        array.SetValue(30, 0, 3);
        array.SetValue(40, 0, 4);
        var observer = new StructuralObservation(new Dictionary<string, string>());

        var value = observer.Capture(array);

        Assert.AreEqual("array", value.Kind);
        Assert.AreEqual("-1:2,3:2", value.Value);
        Assert.AreSequenceEqual(ExpectedArrayIndices, value.Members.Select(member => member.Name));
        Assert.AreSequenceEqual(ExpectedArrayValues, value.Members.Select(member => member.Value.Value));
    }

    /// <summary>
    /// A caller can correct blocked framework source and compare it against the actual original method in fresh workers.
    /// </summary>
    /// <param name="input">The original and edited argument.</param>
    [TestMethod]
    [DataRow(-42)]
    [DataRow(0)]
    [DataRow(42)]
    [DataRow(int.MinValue)]
    public async Task Run_CorrectedFrameworkBodyComparesAgainstActualOriginal(int input)
    {
        var session = new Session();
        var edit = session.PrepareEdit("int32 Math::Abs(int32)", "Copy");
        Assert.IsNotEmpty(edit.Problems);
        var helper = edit.Original.Entries.Single(entry => entry.Instruction?.Op.Name == "call").Instruction!;
        var message = Assert.ThrowsExactly<OverflowException>(() => Math.Abs(int.MinValue)).Message;
        var replacement = "ldstr " + LiteralParser.Escape(message) + "\nnewobj instance void OverflowException::.ctor(string)\nthrow";
        session.CommitEdit(edit.Name, edit.Source.Replace(helper.Text, replacement, StringComparison.Ordinal));

        var result = await Run(session, $"Copy ({input})");

        Assert.AreEqual("match", result.Outcome, Details(result));
        Assert.HasCount(1, result.Original.Invocations);
        Assert.HasCount(1, result.Edited.Invocations);
        if (input == int.MinValue)
        {
            Assert.IsNotNull(result.Original.Exception);
            Assert.EndsWith("System.OverflowException", result.Original.Exception.Type);
            Assert.AreEqual(message, result.Original.Exception.Message);
            Assert.AreEqual(result.Original.Exception, result.Edited.Exception);
        }
        else
        {
            var expected = Math.Abs(input).ToString(CultureInfo.InvariantCulture);
            Assert.AreEqual(expected, result.Original.Result!.Value);
            Assert.AreEqual(expected, result.Edited.Result!.Value);
        }
    }

    private Task<ComparisonReply> Run(Session session, string command) =>
        ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, command), TestContext.CancellationToken);

    private static ObservedValue Field(ObservedValue value, string name) =>
        value.Members.Single(member => member.Name.EndsWith("::" + name, StringComparison.Ordinal)).Value;

    private static string Details(ComparisonReply result) =>
        $"{result.Outcome}: original={result.Original.Outcome} {result.Original.Detail}; "
        + $"edited={result.Edited.Outcome} {result.Edited.Detail}";
}
