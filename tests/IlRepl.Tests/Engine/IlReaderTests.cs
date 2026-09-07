using System.Reflection.Emit;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Tests for <see cref="IlReader"/> and the by-value opcode table, over raw byte sequences.
/// </summary>
[TestClass]
public sealed class IlReaderTests
{
    /// <summary>
    /// One- and two-byte opcodes decode with their offsets and sizes.
    /// </summary>
    [TestMethod]
    public void Read_OneAndTwoByteOpcodes_HaveOffsetsAndSizes()
    {
        var result = IlReader.Read([0x00, 0xFE, 0x01, 0xFE, 0x1C, 0x01, 0x00, 0x00, 0x01, 0xFE, 0x16, 0x02, 0x00, 0x00, 0x01, 0x2A]);
        Assert.IsEmpty(result.Problems);
        var names = result.Instructions.Select(i => (i.Op.Name, i.Offset, i.Size)).ToList();
        Assert.AreSequenceEqual([("nop", 0, 1), ("ceq", 1, 2), ("sizeof", 3, 6), ("constrained.", 9, 6), ("ret", 15, 1)], names);
        Assert.AreEqual(0x01000001, result.Instructions[2].Operand.Token);
    }

    /// <summary>
    /// Every operand layout reads the right number of bytes and the right value.
    /// </summary>
    /// <param name="bytes">The instruction bytes.</param>
    /// <param name="name">The expected opcode.</param>
    /// <param name="size">The expected size.</param>
    /// <param name="integer">The expected integer operand.</param>
    [TestMethod]
    [DataRow(new byte[] { 0x00 }, "nop", 1, 0L)]
    [DataRow(new byte[] { 0x1F, 0xFD }, "ldc.i4.s", 2, -3L)]
    [DataRow(new byte[] { 0xFE, 0x12, 0x04 }, "unaligned.", 3, 4L)]
    [DataRow(new byte[] { 0xFE, 0x19, 0x07 }, "no.", 3, 7L)]
    [DataRow(new byte[] { 0x20, 0x00, 0x00, 0x00, 0x80 }, "ldc.i4", 5, (long)int.MinValue)]
    [DataRow(new byte[] { 0x21, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, "ldc.i8", 9, long.MaxValue)]
    [DataRow(new byte[] { 0x72, 0x01, 0x00, 0x00, 0x70 }, "ldstr", 5, 0x70000001L)]
    [DataRow(new byte[] { 0x11, 0x01 }, "ldloc.s", 2, 1L)]
    [DataRow(new byte[] { 0xFE, 0x0C, 0x2C, 0x01 }, "ldloc", 4, 300L)]
    [DataRow(new byte[] { 0x0E, 0xFF }, "ldarg.s", 2, 255L)]
    [DataRow(new byte[] { 0x28, 0x03, 0x00, 0x00, 0x0A }, "call", 5, 0x0A000003L)]
    [DataRow(new byte[] { 0x7B, 0x01, 0x00, 0x00, 0x04 }, "ldfld", 5, 0x04000001L)]
    [DataRow(new byte[] { 0xD0, 0x02, 0x00, 0x00, 0x01 }, "ldtoken", 5, 0x01000002L)]
    [DataRow(new byte[] { 0x29, 0x01, 0x00, 0x00, 0x11 }, "calli", 5, 0x11000001L)]
    [DataRow(new byte[] { 0x8C, 0x05, 0x00, 0x00, 0x1B }, "box", 5, 0x1B000005L)]
    public void Read_EveryOperandLayout_ReadsItsBytes(byte[] bytes, string name, int size, long integer)
    {
        var result = IlReader.Read(bytes);
        Assert.IsEmpty(result.Problems, string.Join("; ", result.Problems));
        Assert.HasCount(1, result.Instructions);
        var instruction = result.Instructions[0];
        Assert.AreEqual(name, instruction.Op.Name);
        Assert.AreEqual(size, instruction.Size);
        Assert.AreEqual(integer, instruction.Operand.Integer);
    }

    /// <summary>
    /// Floats keep their bits; nothing is widened.
    /// </summary>
    [TestMethod]
    public void Read_Floats_KeepBits()
    {
        var r4 = IlReader.Read([0x22, 0x01, 0x00, 0x80, 0x7F]).Instructions[0];
        Assert.AreEqual("ldc.r4", r4.Op.Name);
        Assert.AreEqual(0x7F800001u, r4.Operand.Bits32);
        var r8 = IlReader.Read([0x23, 0x23, 0x01, 0x00, 0x00, 0x00, 0x00, 0xF8, 0xFF]).Instructions[0];
        Assert.AreEqual("ldc.r8", r8.Op.Name);
        Assert.AreEqual(0xFFF8000000000123ul, r8.Operand.Bits64);
    }

    /// <summary>
    /// Branch targets are relative to the end of the instruction, forward and backward, short and long.
    /// </summary>
    [TestMethod]
    public void Read_Branches_AreRelativeToNextInstruction()
    {
        var forward = IlReader.Read([0x00, 0x2B, 0x01, 0x00, 0x2A]);
        Assert.IsEmpty(forward.Problems);
        Assert.AreEqual(4, forward.Instructions[1].BranchTarget);
        Assert.AreEqual(1L, forward.Instructions[1].Operand.Integer);

        var backward = IlReader.Read([0x00, 0x2B, 0xFD]);
        Assert.IsEmpty(backward.Problems);
        Assert.AreEqual(0, backward.Instructions[1].BranchTarget);

        var longForm = IlReader.Read([0x38, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2A]);
        Assert.IsEmpty(longForm.Problems);
        Assert.AreEqual(6, longForm.Instructions[0].BranchTarget);
        Assert.AreEqual("IL_0006", IlReader.LabelFor(longForm.Instructions[0].BranchTarget!.Value));
    }

    /// <summary>
    /// Switch targets are relative to the end of the whole table.
    /// </summary>
    [TestMethod]
    public void Read_Switch_TargetsAreRelativeToEndOfTable()
    {
        // nop; switch (3 targets: +1, +2, -18); nop; nop; nop
        byte[] il = [0x00, 0x45, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0xEE, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00];
        var result = IlReader.Read(il);
        Assert.IsEmpty(result.Problems, string.Join("; ", result.Problems));
        var sw = result.Instructions[1];
        Assert.AreEqual("switch", sw.Op.Name);
        Assert.AreEqual(17, sw.Size);
        Assert.AreEqual(18, sw.Next);
        Assert.AreSequenceEqual([19, 20, 0], sw.Operand.SwitchTargets);
        Assert.HasCount(5, result.Instructions);
    }

    /// <summary>
    /// The no. prefix decodes with its mask and the next instruction lands at the right offset.
    /// </summary>
    /// <param name="mask">The mask byte.</param>
    [TestMethod]
    [DataRow((byte)1)]
    [DataRow((byte)2)]
    [DataRow((byte)4)]
    [DataRow((byte)7)]
    public void Read_NoPrefix_DecodesMaskAndKeepsOffsets(byte mask)
    {
        var result = IlReader.Read([0xFE, 0x19, mask, 0x14, 0x2A]);
        Assert.IsEmpty(result.Problems);
        Assert.AreEqual("no.", result.Instructions[0].Op.Name);
        Assert.IsNull(result.Instructions[0].Op.Emit);
        Assert.IsTrue(result.Instructions[0].Op.IsSkipChecksPrefix);
        Assert.AreEqual(mask, result.Instructions[0].Operand.Integer);
        Assert.AreEqual(("ldnull", 3), (result.Instructions[1].Op.Name, result.Instructions[1].Offset));
        Assert.AreEqual(("ret", 4), (result.Instructions[2].Op.Name, result.Instructions[2].Offset));
    }

    /// <summary>
    /// A fault stops decoding with a problem naming its offset and keeps what came before.
    /// </summary>
    /// <param name="bytes">The body.</param>
    /// <param name="decoded">How many instructions decode before the fault.</param>
    /// <param name="problem">A fragment of the expected problem.</param>
    [TestMethod]
    [DataRow(new byte[] { 0x00, 0x28 }, 1, "truncated operand at IL_0001")]
    [DataRow(new byte[] { 0x00, 0xFE }, 1, "truncated opcode at IL_0001")]
    [DataRow(new byte[] { 0x00, 0x45, 0x02, 0x00, 0x00, 0x00, 0x01 }, 1, "declares 2 targets")]
    [DataRow(new byte[] { 0x00, 0x45, 0xFF, 0xFF, 0xFF, 0x7F }, 1, "declares 2147483647 targets")]
    [DataRow(new byte[] { 0x00, 0x45, 0x01 }, 1, "truncated switch at IL_0001")]
    [DataRow(new byte[] { 0x2A, 0xF8 }, 1, "reserved opcode 0xf8 at IL_0001")]
    [DataRow(new byte[] { 0x2A, 0xFE, 0x30 }, 1, "unknown opcode 0xfe30 at IL_0001")]
    public void Read_Faults_StopWithOffsetAndKeepHead(byte[] bytes, int decoded, string problem)
    {
        var result = IlReader.Read(bytes);
        Assert.HasCount(decoded, result.Instructions);
        Assert.HasCount(1, result.Problems);
        Assert.Contains(problem, result.Problems[0]);
    }

    /// <summary>
    /// A target that is not the start of an instruction, or past the end, is a problem but the
    /// instructions are all kept.
    /// </summary>
    [TestMethod]
    public void Read_TargetsOffInstructionBoundaries_AreProblems()
    {
        // br.s +1 lands inside the ldc.i4 that follows.
        var inside = IlReader.Read([0x2B, 0x01, 0x20, 0x01, 0x00, 0x00, 0x00, 0x2A]);
        Assert.HasCount(3, inside.Instructions);
        Assert.HasCount(1, inside.Problems);
        Assert.Contains("br.s at IL_0000 targets IL_0003, which is not the start of an instruction", inside.Problems[0]);

        var past = IlReader.Read([0x2B, 0x05]);
        Assert.HasCount(1, past.Instructions);
        Assert.Contains("targets IL_0007", past.Problems[0]);

        // A switch target on the end of the body (a clause end could sit there, a branch cannot).
        var atEnd = IlReader.Read([0x45, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        Assert.HasCount(1, atEnd.Problems);
        Assert.Contains("switch at IL_0000 target 0 is IL_0009", atEnd.Problems[0]);
    }

    /// <summary>
    /// A branch whose relative offset overflows is a fault, not an exception.
    /// </summary>
    [TestMethod]
    public void Read_OverflowingBranch_IsProblem()
    {
        var result = IlReader.Read([0x38, 0xFF, 0xFF, 0xFF, 0x7F]);
        Assert.IsEmpty(result.Instructions);
        Assert.Contains("overflows", result.Problems[0]);
    }

    /// <summary>
    /// Every named opcode that can be encoded is in the by-value table and back, and no. is only there.
    /// </summary>
    [TestMethod]
    public void ByValue_RoundTripsByName_AndAddsNoPrefix()
    {
        foreach (var (name, op) in OpcodeTable.ByName)
        {
            if (OpcodeTable.IsReserved(name))
            {
                Assert.IsFalse(OpcodeTable.TryGetByValue(unchecked((ushort)op.Value), out _), name);
                continue;
            }

            Assert.IsTrue(OpcodeTable.TryGetByValue(unchecked((ushort)op.Value), out var found), name);
            Assert.AreEqual(name, found.Name);
            Assert.AreEqual(op, found.Emit);
            Assert.AreEqual(op.OperandType, found.OperandType);
            Assert.AreEqual(op.Size, found.Size);
        }

        Assert.IsTrue(OpcodeTable.TryGetByValue(OpcodeTable.NoPrefixValue, out var no));
        Assert.AreEqual("no.", no.Name);
        Assert.IsFalse(OpcodeTable.ByName.ContainsKey("no."));
        Assert.HasCount(OpcodeTable.ByName.Count - 8 + 1, OpcodeTable.ByValue);
    }

    /// <summary>
    /// Operand sizes follow the layout table; switch is variable.
    /// </summary>
    [TestMethod]
    public void OperandSize_FollowsLayout()
    {
        Assert.AreEqual(0, OpcodeTable.OperandSize(OperandType.InlineNone));
        Assert.AreEqual(1, OpcodeTable.OperandSize(OperandType.ShortInlineVar));
        Assert.AreEqual(2, OpcodeTable.OperandSize(OperandType.InlineVar));
        Assert.AreEqual(4, OpcodeTable.OperandSize(OperandType.ShortInlineR));
        Assert.AreEqual(8, OpcodeTable.OperandSize(OperandType.InlineR));
        Assert.AreEqual(-1, OpcodeTable.OperandSize(OperandType.InlineSwitch));
    }
}
