using IlRepl.Tui;

namespace IlRepl.Tests.Tui;

/// <summary>
/// The status bar keeps the hints that fit beside the other sections and drops the rest from
/// the left, and it shows copy mode's keys while copy mode is on.
/// </summary>
[TestClass]
public sealed class StatusHintsTests
{
    private static readonly string[] s_facts = ["stack [int32, int32]", "no locals", "2 instructions"];
    private static readonly string[] s_all = ["Tab complete", "Shift+↑ select", "Ctrl+Q quit"];
    private static readonly string[] s_two = ["Shift+↑ select", "Ctrl+Q quit"];
    private static readonly string[] s_one = ["Ctrl+Q quit"];
    private static readonly string[] s_copyAll = ["Shift+↑↓ extend", "y yank", "Esc cancel"];
    private static readonly string[] s_copyTwo = ["y yank", "Esc cancel"];
    private static readonly string[] s_copyOne = ["Esc cancel"];

    /// <summary>
    /// Every hint fits in a wide terminal; a narrow one keeps the last hint alone.
    /// </summary>
    [TestMethod]
    public void Hints_DropFromTheLeftAsWidthShrinks()
    {
        Assert.AreSequenceEqual(s_all, IlReplApp.StatusHints(s_facts, 100, copyMode: false));
        Assert.AreSequenceEqual(s_two, IlReplApp.StatusHints(s_facts, 83, copyMode: false));
        Assert.AreSequenceEqual(s_one, IlReplApp.StatusHints(s_facts, 60, copyMode: false));
        Assert.AreSequenceEqual(s_one, IlReplApp.StatusHints(s_facts, 20, copyMode: false));
    }

    /// <summary>
    /// Copy mode shows its own keys, dropped the same way.
    /// </summary>
    [TestMethod]
    public void CopyMode_ShowsItsKeys()
    {
        Assert.AreSequenceEqual(s_copyAll, IlReplApp.StatusHints(s_facts, 100, copyMode: true));
        Assert.AreSequenceEqual(s_copyTwo, IlReplApp.StatusHints(s_facts, 83, copyMode: true));
        Assert.AreSequenceEqual(s_copyOne, IlReplApp.StatusHints(s_facts, 20, copyMode: true));
    }

    /// <summary>
    /// Before the size is known nothing is dropped.
    /// </summary>
    [TestMethod]
    public void UnknownWidth_KeepsEveryHint()
    {
        Assert.HasCount(3, IlReplApp.StatusHints(s_facts, 0, copyMode: false));
    }
}
