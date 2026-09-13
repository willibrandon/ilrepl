using IlRepl.Engine;
using IlRepl.Host;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Direct comparison capture checks the literals against both actual method signatures.
/// </summary>
[TestClass]
public sealed class DirectComparisonArgumentTests
{
    /// <summary>
    /// Supplies cancellation for actual comparison workers.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Adding or removing parameters cannot export an original with missing or unused arguments.
    /// </summary>
    /// <param name="originalCount">The original method's parameter count.</param>
    /// <param name="editedCount">The edited method's parameter count.</param>
    [TestMethod]
    [DataRow(2, 1)]
    [DataRow(1, 0)]
    [DataRow(1, 2)]
    [DataRow(0, 1)]
    public void Create_DifferentParameterCounts_RejectsBeforeExport(int originalCount, int editedCount)
    {
        static string Parameters(int count) => string.Join(", ", Enumerable.Range(0, count).Select(index => "int32 value" + index));
        var session = IlLines.Load(".method int32 Read(" + Parameters(originalCount) + ") { ldc.i4.s 41; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name,
            ".method public static int32 Read(" + Parameters(editedCount) + ") cil managed {\nldc.i4.s 42\nret\n}");
        var literals = string.Join(", ", Enumerable.Repeat("1", editedCount));
        var generation = session.Generation;

        var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy (" + literals + ")"));

        var noun = originalCount == 1 ? "argument" : "arguments";
        Assert.AreEqual($"the original Copy requires {originalCount} literal {noun}; received {editedCount}", error.Message);
        Assert.AreEqual(generation, session.Generation);
        Assert.AreEqual(1, edit.Revision);
        Assert.AreEqual(41, edit.OriginalMethod.Invoke(null, Enumerable.Repeat<object>(1, originalCount).ToArray()));
        Assert.AreEqual(42, edit.Method!.Invoke(null, Enumerable.Repeat<object>(1, editedCount).ToArray()));
    }

    /// <summary>
    /// A literal that fits only the edited parameter is rejected with the original argument's diagnostic.
    /// </summary>
    /// <param name="byReference">Whether the original parameter is passed by reference.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Create_LiteralOutsideOriginalRange_RejectsBeforeExport(bool byReference)
    {
        var session = IlLines.Load(".method int32 Read(uint8" + (byReference ? "&" : "") + " value) { ldc.i4.s 41; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ".method public static int32 Read(int32 value) cil managed {\nldarg.0\nret\n}");

        var error = Assert.ThrowsExactly<ReplException>(() => ComparisonCapture.Create(session, "Copy (256)"));

        Assert.StartsWith("original argument 1: '256' does not fit uint8:", error.Message);
        Assert.AreEqual(256, edit.Method!.Invoke(null, [256]));
        Assert.AreEqual(41, edit.OriginalMethod.Invoke(null, [(byte)255]));
    }

    /// <summary>
    /// Literals valid for both changed parameter types still execute and expose their distinct typed inputs.
    /// </summary>
    /// <returns>The completed worker and input assertions.</returns>
    [TestMethod]
    public async Task Create_LiteralFitsBothParameterTypes_RemainsSupported()
    {
        var session = IlLines.Load(".method int32 Read(uint8 value) { ldarg.0; ret }");
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ".method public static int32 Read(int32 value) cil managed {\nldarg.0\nret\n}");

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy (42)"),
            TestContext.CancellationToken);

        Assert.AreEqual("different-inputs", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        foreach (var side in new[] { result.Original, result.Edited })
        {
            Assert.AreEqual("completed", side.Outcome, side.Detail);
            Assert.AreEqual("42", side.Result!.Value);
            Assert.HasCount(1, side.Invocations);
            var input = side.Invocations[0].Inputs.Single(member => member.Name == "argument 0").Value;
            Assert.AreEqual("42", input.Value);
            Assert.EndsWith(side == result.Original ? "System.Byte" : "System.Int32", input.Type);
        }
    }
}
