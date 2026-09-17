using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies native differences preserve alignment and conventional unified hunk ranges and context.
/// </summary>
[TestClass]
public sealed class NativeDifferenceTests
{
    /// <summary>
    /// Distant instruction changes retain exactly three unchanged lines and omit the unrelated middle region.
    /// </summary>
    [TestMethod]
    public void Create_DistantChangesProduceSeparateUnifiedHunks()
    {
        var left = Enumerable.Range(1, 20).Select(value => "mov eax, " + value).ToArray();
        var right = left.ToArray();
        right[3] = "mov eax, 40";
        right[15] = "mov eax, 160";

        var difference = NativeDifference.Create(left, right, "Copy (original)", "Copy (edited)");

        Assert.AreSequenceEqual(
        [
            "--- Copy (original)", "+++ Copy (edited)", "@@ -1,7 +1,7 @@",
            " mov eax, 1", " mov eax, 2", " mov eax, 3", "-mov eax, 4", "+mov eax, 40",
            " mov eax, 5", " mov eax, 6", " mov eax, 7", "@@ -13,7 +13,7 @@",
            " mov eax, 13", " mov eax, 14", " mov eax, 15", "-mov eax, 16", "+mov eax, 160",
            " mov eax, 17", " mov eax, 18", " mov eax, 19",
        ], difference);
    }

    /// <summary>
    /// Insertion and deletion at an empty boundary use zero ranges and preserve original instruction indentation.
    /// </summary>
    /// <param name="insertion">Whether the only instruction is added or removed.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Create_EmptyBoundaryUsesZeroRangeWithoutChangingContent(bool insertion)
    {
        string[] instruction = ["    ret"];

        var difference = NativeDifference.Create(insertion ? [] : instruction, insertion ? instruction : [], "left", "right");

        Assert.AreSequenceEqual(
        [
            "--- left", "+++ right", insertion ? "@@ -0,0 +1,1 @@" : "@@ -1,1 +0,0 @@",
            (insertion ? "+" : "-") + instruction[0],
        ], difference);
    }

    /// <summary>
    /// Equivalent instruction sequences produce no headers or false difference when only side labels differ.
    /// </summary>
    [TestMethod]
    public void Create_EqualInstructionsHaveNoDifference()
    {
        string[] instructions = ["L01:", "    mov eax, 42", "    ret"];

        Assert.IsEmpty(NativeDifference.Create(instructions, instructions.ToArray(), "left: Read", "right: Read"));
    }
}
