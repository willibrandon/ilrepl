using System.Buffers.Binary;
using System.Globalization;
using System.Reflection.Emit;

namespace IlRepl.Engine;

/// <summary>
/// Decodes the bytes of a method body into instructions. Pure: it reads opcodes and operand
/// layouts, computes branch and switch targets, and checks that every target is the start of an
/// instruction; it never touches metadata or the runtime.
/// </summary>
public static class IlReader
{
    /// <summary>
    /// Formats an offset as an ildasm label, <c>IL_0004</c>.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The label.</returns>
    public static string LabelFor(int offset) => "IL_" + offset.ToString("x4", CultureInfo.InvariantCulture);

    /// <summary>
    /// Decodes a method body.
    /// </summary>
    /// <param name="il">The body bytes.</param>
    /// <returns>The instructions and any faults.</returns>
    public static IlReadResult Read(ReadOnlySpan<byte> il)
    {
        var instructions = new List<RawInstruction>();
        var problems = new List<string>();
        var offset = 0;
        while (offset < il.Length)
        {
            var start = offset;
            ushort value = il[offset++];
            if (value == 0xFE)
            {
                if (offset >= il.Length)
                {
                    problems.Add($"truncated opcode at {LabelFor(start)}: 0xFE is the first byte of a two-byte opcode");
                    break;
                }

                value = (ushort)(0xFE00 | il[offset++]);
            }

            if (!OpcodeTable.TryGetByValue(value, out var op))
            {
                problems.Add(value is >= 0xF8 and <= 0xFF
                    ? $"reserved opcode 0x{value:x2} at {LabelFor(start)}"
                    : $"unknown opcode 0x{value:x2} at {LabelFor(start)}");
                break;
            }

            var operandSize = op.OperandSize;
            if (op.OperandType == OperandType.InlineSwitch)
            {
                if (il.Length - offset < 4)
                {
                    problems.Add($"truncated switch at {LabelFor(start)}");
                    break;
                }

                var count = BinaryPrimitives.ReadInt32LittleEndian(il[offset..]);
                offset += 4;
                if (count < 0 || count > (il.Length - offset) / 4)
                {
                    problems.Add($"switch at {LabelFor(start)} declares {count} targets but only {(il.Length - offset) / 4} fit in the body");
                    break;
                }

                var tableEnd = offset + (count * 4);
                var targets = new int[count];
                var bad = false;
                for (var i = 0; i < count; i++)
                {
                    var rel = BinaryPrimitives.ReadInt32LittleEndian(il[(offset + (i * 4))..]);
                    if (!TryTarget(tableEnd, rel, out targets[i]))
                    {
                        problems.Add($"switch target {i} at {LabelFor(start)} overflows");
                        bad = true;
                        break;
                    }
                }

                if (bad)
                {
                    break;
                }

                instructions.Add(new RawInstruction(start, tableEnd - start, op, new RawOperand(count, 0, 0, targets)));
                offset = tableEnd;
                continue;
            }

            if (il.Length - offset < operandSize)
            {
                problems.Add($"truncated operand at {LabelFor(start)}: '{op.Name}' needs {operandSize} byte{(operandSize == 1 ? "" : "s")} but {il.Length - offset} remain");
                break;
            }

            var bytes = il.Slice(offset, operandSize);
            var next = offset + operandSize;
            var operand = RawOperand.None;
            int? target = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                {
                    var rel = (sbyte)bytes[0];
                    if (!TryTarget(next, rel, out var t))
                    {
                        problems.Add($"branch at {LabelFor(start)} overflows");
                        goto stop;
                    }

                    target = t;
                    operand = new RawOperand(rel, 0, 0, []);
                    break;
                }

                case OperandType.InlineBrTarget:
                {
                    var rel = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                    if (!TryTarget(next, rel, out var t))
                    {
                        problems.Add($"branch at {LabelFor(start)} overflows");
                        goto stop;
                    }

                    target = t;
                    operand = new RawOperand(rel, 0, 0, []);
                    break;
                }

                case OperandType.ShortInlineI:
                    // ldc.i4.s takes a signed byte; unaligned. and no. take an unsigned one.
                    operand = new RawOperand(op.Value == 0x1F ? (sbyte)bytes[0] : bytes[0], 0, 0, []);
                    break;
                case OperandType.ShortInlineVar:
                    operand = new RawOperand(bytes[0], 0, 0, []);
                    break;
                case OperandType.InlineVar:
                    operand = new RawOperand(BinaryPrimitives.ReadUInt16LittleEndian(bytes), 0, 0, []);
                    break;
                case OperandType.InlineI:
                    operand = new RawOperand(BinaryPrimitives.ReadInt32LittleEndian(bytes), 0, 0, []);
                    break;
                case OperandType.InlineI8:
                    operand = new RawOperand(BinaryPrimitives.ReadInt64LittleEndian(bytes), 0, 0, []);
                    break;
                case OperandType.ShortInlineR:
                    operand = new RawOperand(0, BinaryPrimitives.ReadUInt32LittleEndian(bytes), 0, []);
                    break;
                case OperandType.InlineR:
                    operand = new RawOperand(0, 0, BinaryPrimitives.ReadUInt64LittleEndian(bytes), []);
                    break;
                default:
                    // InlineString, InlineField, InlineMethod, InlineType, InlineTok, InlineSig: a token.
                    operand = new RawOperand(BinaryPrimitives.ReadUInt32LittleEndian(bytes), 0, 0, []);
                    break;
            }

            instructions.Add(new RawInstruction(start, next - start, op, operand) { BranchTarget = target });
            offset = next;
        }

    stop:
        var starts = new HashSet<int>(instructions.Select(i => i.Offset));
        foreach (var instruction in instructions)
        {
            if (instruction.BranchTarget is int t && !starts.Contains(t))
            {
                problems.Add($"{instruction.Op.Name} at {instruction.Label} targets {LabelFor(t)}, which is not the start of an instruction");
            }

            for (var i = 0; i < instruction.Operand.SwitchTargets.Length; i++)
            {
                if (!starts.Contains(instruction.Operand.SwitchTargets[i]))
                {
                    problems.Add($"switch at {instruction.Label} target {i} is {LabelFor(instruction.Operand.SwitchTargets[i])}, which is not the start of an instruction");
                }
            }
        }

        return new IlReadResult(instructions, problems);
    }

    private static bool TryTarget(int next, int rel, out int target)
    {
        try
        {
            target = checked(next + rel);
            return target >= 0;
        }
        catch (OverflowException)
        {
            target = 0;
            return false;
        }
    }
}
