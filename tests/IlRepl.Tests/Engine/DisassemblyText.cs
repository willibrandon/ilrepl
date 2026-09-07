using System.Globalization;
using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Renders a disassembled method the way the tests want to read it: one line per entry with
/// the offset in hex, labels bare, block boundaries as their words, and the stack column beside.
/// </summary>
internal static class DisassemblyText
{
    /// <summary>
    /// The listing lines without the stack column.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>The lines.</returns>
    public static List<string> Lines(DisassembledMethod method) => method.Entries.Select(Line).ToList();

    /// <summary>
    /// The listing lines with the stack column after a tab.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>The lines.</returns>
    public static List<string> LinesWithStack(DisassembledMethod method)
    {
        var column = StackAnalysis.Run(method);
        return method.Entries.Select((e, i) => column[i] is null ? Line(e) : Line(e) + "\t" + column[i]).ToList();
    }

    /// <summary>
    /// The instruction and raw lines only, without offsets.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>The texts.</returns>
    public static List<string> Instructions(DisassembledMethod method) =>
        method.Entries.Where(e => e.Kind is DisassembledEntryKind.Instruction or DisassembledEntryKind.Raw).Select(e => e.DisplayText).ToList();

    /// <summary>
    /// The stack column entry for the instruction at an offset.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="offset">The instruction offset.</param>
    /// <returns>The column text.</returns>
    public static string StackAt(DisassembledMethod method, int offset)
    {
        var column = StackAnalysis.Run(method);
        for (var i = 0; i < method.Entries.Count; i++)
        {
            if (method.Entries[i].Kind is DisassembledEntryKind.Instruction or DisassembledEntryKind.Raw && method.Entries[i].Offset == offset)
            {
                return column[i]!;
            }
        }

        throw new AssertFailedException("no instruction at " + IlReader.LabelFor(offset));
    }

    private static string Line(DisassembledEntry entry) => entry.Kind switch
    {
        DisassembledEntryKind.Label => entry.Label + ":",
        DisassembledEntryKind.Block => entry.Block switch
        {
            BlockKind.Try => ".try {",
            BlockKind.Catch => "} catch " + entry.CatchText + " {",
            BlockKind.Filter => "} filter {",
            BlockKind.FilterHandler => "} handler {",
            BlockKind.Finally => "} finally {",
            BlockKind.Fault => "} fault {",
            _ => "}",
        },
        _ => entry.Offset.ToString("x4", CultureInfo.InvariantCulture) + " " + entry.DisplayText,
    };
}
