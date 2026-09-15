using IlRepl.Engine;
using IlRepl.Host;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Structural comparisons preserve array kinds inside values, reflected types, and generic arguments.
/// </summary>
[TestClass]
public sealed class ArrayKindObservationTests
{
    /// <summary>
    /// Supplies cancellation for the real comparison processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Identical array contents cannot conceal different runtime types or different casting behavior.
    /// </summary>
    /// <param name="kind">The array value or reflected type shape.</param>
    /// <param name="before">The expected original array type suffix.</param>
    /// <param name="after">The expected edited array type suffix.</param>
    [TestMethod]
    [DataRow("array", "System.Int32[][]", "System.Int32[*][]")]
    [DataRow("bounds", "System.Int32[]", "System.Int32[*]")]
    [DataRow("type", "System.Int32[]", "System.Int32[*]")]
    [DataRow("jagged", "System.Int32[][]", "System.Int32[*][]")]
    [DataRow("generic", "System.Int32[]>", "System.Int32[*]>")]
    public async Task Compare_ArrayKindsRemainDistinct(string kind, string before, string after)
    {
        var session = IlLines.Load(ArrayKindExamples.Source(kind, false).Split('\n'));
        var edit = session.PrepareEdit("Read", "Copy");
        session.CommitEdit(edit.Name, ArrayKindExamples.Source(kind, true));
        var original = edit.OriginalMethod.Invoke(null, null);
        var edited = edit.Method!.Invoke(null, null);
        if (kind == "array")
        {
            Assert.IsInstanceOfType<int[][]>(original);
            Assert.IsNotInstanceOfType<int[][]>(edited);
            Assert.AreEqual(typeof(int).MakeArrayType(1).MakeArrayType(), edited!.GetType());
        }

        if (original is Array first && edited is Array second)
        {
            Assert.HasCount(2, first);
            Assert.HasCount(2, second);
            Assert.AreSequenceEqual(first.Cast<object?>(), second.Cast<object?>());
        }

        var result = await ProcessComparisonRunner.RunAsync(ComparisonCapture.Create(session, "Copy ()"),
            TestContext.CancellationToken);
        Assert.AreEqual("different", result.Outcome, result.Original.Detail + "; " + result.Edited.Detail);
        var originalResult = result.Original.Result!;
        var editedResult = result.Edited.Result!;
        var arrays = kind is "array" or "bounds";
        Assert.EndsWith(before, arrays ? originalResult.Type : originalResult.Value!);
        Assert.EndsWith(after, arrays ? editedResult.Type : editedResult.Value!);
        if (arrays)
        {
            Assert.AreEqual("0:2", originalResult.Value);
            Assert.AreEqual(kind == "bounds" ? "-1:2" : "0:2", editedResult.Value);
            Assert.HasCount(2, originalResult.Members);
            Assert.AreSequenceEqual(originalResult.Members.Select(member => (member.Name, member.Value.Kind, member.Value.Value)),
                editedResult.Members.Select(member => (member.Name, member.Value.Kind, member.Value.Value)));
        }
    }
}
