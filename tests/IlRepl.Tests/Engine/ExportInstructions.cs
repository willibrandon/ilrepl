using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Reads control flow independently of the exporter and normalizes metadata tokens and instruction offsets.
/// </summary>
internal static class ExportInstructions
{
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .DistinctBy(code => code.Value).ToDictionary(code => code.Value);

    /// <summary>
    /// Determines whether initialization affects stack-allocated memory even without declared locals.
    /// </summary>
    internal static bool HasLocalloc(MethodBodyBlock body) => Decode(body).Any(instruction => instruction.Code == OpCodes.Localloc);

    /// <summary>
    /// Records instructions, branch destinations, and protected regions using stable instruction indexes.
    /// </summary>
    internal static IEnumerable<string> Read(MethodBodyBlock body, Func<int, string> token)
    {
        var instructions = Decode(body);
        var offsets = instructions.Select((instruction, index) => (instruction.Offset, index))
            .ToDictionary(pair => pair.Offset, pair => pair.index);
        offsets[body.GetILBytes()!.Length] = instructions.Count;
        foreach (var (offset, code, operand) in instructions)
        {
            var value = code.OperandType switch
            {
                OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget => "target " + offsets[(int)operand!],
                OperandType.InlineSwitch => string.Join(',', ((int[])operand!).Select(destination => offsets[destination])),
                OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                    or OperandType.InlineTok or OperandType.InlineType => token((int)operand!),
                _ => Convert.ToString(operand, CultureInfo.InvariantCulture),
            };
            yield return "instruction " + offsets[offset] + " " + code.Name + " " + value;
        }

        foreach (var region in body.ExceptionRegions)
        {
            yield return "region " + region.Kind + " " + offsets[region.TryOffset] + ":"
                + offsets[region.TryOffset + region.TryLength] + " " + offsets[region.HandlerOffset] + ":"
                + offsets[region.HandlerOffset + region.HandlerLength]
                + (region.Kind == ExceptionRegionKind.Filter ? " filter " + offsets[region.FilterOffset] : "");
        }
    }

    private static List<(int Offset, OpCode Code, object? Operand)> Decode(MethodBodyBlock body)
    {
        var bytes = body.GetILBytes() ?? [];
        var result = new List<(int, OpCode, object?)>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var start = offset;
            var first = bytes[offset++];
            var code = Codes[first == 0xfe ? unchecked((short)(0xfe00 | bytes[offset++])) : first];
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4,
            };
            var end = offset + size;
            object? operand = code.OperandType switch
            {
                OperandType.InlineNone => null,
                OperandType.ShortInlineI => (sbyte)bytes[offset],
                OperandType.ShortInlineVar => bytes[offset],
                OperandType.InlineVar => BitConverter.ToUInt16(bytes, offset),
                OperandType.ShortInlineBrTarget => end + (sbyte)bytes[offset],
                OperandType.InlineBrTarget => end + BitConverter.ToInt32(bytes, offset),
                OperandType.InlineSwitch => Enumerable.Range(0, BitConverter.ToInt32(bytes, offset))
                    .Select(index => end + BitConverter.ToInt32(bytes, offset + 4 + 4 * index)).ToArray(),
                OperandType.InlineR or OperandType.ShortInlineR => Convert.ToHexString(bytes.AsSpan(offset, size)),
                OperandType.InlineI8 => BitConverter.ToInt64(bytes, offset),
                _ => BitConverter.ToInt32(bytes, offset),
            };
            result.Add((start, code, operand));
            offset = end;
        }

        return result;
    }
}
