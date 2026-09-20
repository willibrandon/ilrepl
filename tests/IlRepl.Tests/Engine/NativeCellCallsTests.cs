using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// An x64 call compares equal whether the JIT reached the callee's cell directly or had to load its address first.
/// </summary>
[TestClass]
public sealed class NativeCellCallsTests
{
    private const string First = "<entry-point-cell:ilrepl.types.1, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
        + "!int32 Owner::First()>";

    private const string Second = "<entry-point-cell:ilrepl.types.1, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
        + "!int32 Owner::Second()>";

    /// <summary>
    /// A load that only feeds the next call folds into the direct call, and the rest of the listing stays as it was.
    /// </summary>
    [TestMethod]
    public void Fold_LoadedCallBecomesTheDirectCall()
    {
        string[] loaded =
        [
            "L01:", "    push     rax", "L02:", "    mov      rax, " + First, "    call     [rax]Owner:First():int",
            "    inc      eax", "L03:", "    add      rsp, 8", "    ret",
        ];

        string[] direct =
        [
            "L01:", "    push     rax", "L02:", "    call     [Owner:First():int]", "    inc      eax", "L03:",
            "    add      rsp, 8", "    ret",
        ];

        Assert.AreSequenceEqual(direct, NativeCellCalls.Fold(loaded));
        Assert.AreSequenceEqual(direct, NativeCellCalls.Fold(direct));
    }

    /// <summary>
    /// A tail call's load has a group of its own, so its label goes with it and the jumps past it are renumbered.
    /// </summary>
    [TestMethod]
    public void Fold_TailCallsLoseTheGroupThatHeldTheLoad()
    {
        string[] loaded =
        [
            "L01:", "L02:", "    test     edi, edi", "    jne      SHORT L05", "L03:", "    mov      rax, " + Second, "L04:",
            "    tail.jmp [rax]Owner:Second():int", "L05:", "    mov      rax, " + First, "L06:",
            "    tail.jmp [rax]Owner:First():int",
        ];

        Assert.AreSequenceEqual(
            [
                "L01:", "L02:", "    test     edi, edi", "    jne      SHORT L04", "L03:", "    tail.jmp [Owner:Second():int]",
                "L04:", "    tail.jmp [Owner:First():int]",
            ],
            NativeCellCalls.Fold(loaded));
    }

    /// <summary>
    /// The JIT puts a nop between a call and an epilog, and the load stands in that place when there is one.
    /// </summary>
    [TestMethod]
    public void Fold_LoadBetweenACallAndTheEpilogBecomesTheNop()
    {
        string[] loaded =
        [
            "L01:", "    push     rax", "L02:", "    mov      rax, " + Second, "    call     [rax]Owner:Second():int",
            "    mov      rax, " + First, "L03:", "    add      rsp, 8", "    tail.jmp [rax]Owner:First():int",
        ];

        Assert.AreSequenceEqual(
            [
                "L01:", "    push     rax", "L02:", "    call     [Owner:Second():int]", "    nop", "L03:",
                "    add      rsp, 8", "    tail.jmp [Owner:First():int]",
            ],
            NativeCellCalls.Fold(loaded));
    }

    /// <summary>
    /// A jump table entry counts the bytes to its case, which the longer form changes, so the case label stands for it.
    /// </summary>
    [TestMethod]
    public void Fold_JumpTableEntriesAreReadByTheirCase()
    {
        string[] loaded =
        [
            "L01:", "    jmp      rcx", "L02:", "    mov      rax, " + First, "L03:", "    tail.jmp [rax]Owner:First():int",
            "L04:", "    ret", "RWD00  \tdd\t0000003Bh ; case L04", "       \tdd\t0000001Dh ; case L02",
        ];

        string[] direct =
        [
            "L01:", "    jmp      rcx", "L02:", "    tail.jmp [Owner:First():int]", "L03:", "    ret",
            "RWD00  \tdd\t0000002Eh ; case L03", "       \tdd\t0000001Dh ; case L02",
        ];

        Assert.AreSequenceEqual(NativeCellCalls.Fold(direct), NativeCellCalls.Fold(loaded));
        Assert.AreEqual("RWD00  \tdd\t<code:L03> ; case L03", NativeCellCalls.Fold(loaded)[^2]);
    }

    /// <summary>
    /// A load stays when anything else reads the register, when a jump can arrive after it, or when it is not a call cell.
    /// </summary>
    /// <param name="between">The line between the load and the call.</param>
    /// <param name="address">The loaded address.</param>
    [TestMethod]
    [DataRow("    mov      rcx, qword ptr [rax]", First)]
    [DataRow("    add      eax, 1", First)]
    [DataRow("    call     [Owner:Second():int]", First)]
    [DataRow("    nop", "<method-handle:Owner::First()>")]
    public void Fold_KeepsALoadThatIsNotTheCallsAlone(string between, string address)
    {
        string[] lines = ["L01:", "    mov      rax, " + address, between, "    call     [rax]Owner:First():int", "    ret"];

        Assert.AreSequenceEqual(lines, NativeCellCalls.Fold(lines));
    }

    /// <summary>
    /// A label that a jump names keeps the load in place, because that jump arrives without it.
    /// </summary>
    [TestMethod]
    public void Fold_KeepsALoadBeforeAJumpTarget()
    {
        string[] lines =
        [
            "L01:", "    je       SHORT L02", "    mov      rax, " + First, "L02:", "    tail.jmp [rax]Owner:First():int",
        ];

        Assert.AreSequenceEqual(lines, NativeCellCalls.Fold(lines));
    }
}
