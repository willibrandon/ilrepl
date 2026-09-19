using System.Runtime.InteropServices;

namespace IlRepl.Engine;

/// <summary>
/// Recognizes runtime entry stubs whose instructions prove the location of a method's target cell.
/// </summary>
internal static class NativeEntryPoint
{
    /// <summary>
    /// Reads a known live method entry point and returns its proven indirection cell, or zero for an unrecognized stub.
    /// </summary>
    /// <param name="entry">The entry point obtained from the method's runtime handle.</param>
    /// <returns>The target cell address when the entry instructions establish it.</returns>
    internal static nint IndirectionCell(nint entry)
    {
        if (entry == 0 || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            return 0;
        }

        var first = unchecked((uint)Marshal.ReadInt32(entry));
        if ((first & 0xFF00001F) is not (0x5800000A or 0x5800000B))
        {
            return 0;
        }

        var second = unchecked((uint)Marshal.ReadInt32(entry, 4));
        var third = (first & 31) == 10 ? unchecked((uint)Marshal.ReadInt32(entry, 8)) : 0;
        var offset = Arm64TargetOffset(first, second, third);
        return offset.HasValue ? entry + offset.Value : 0;
    }

    /// <summary>
    /// Decodes the PC-relative target load only when the surrounding instructions match a CoreCLR Arm64 entry stub.
    /// </summary>
    /// <param name="first">The first instruction at the entry point.</param>
    /// <param name="second">The next instruction in the stub.</param>
    /// <param name="third">The third instruction for the regular stub form.</param>
    /// <returns>The signed byte displacement of the target cell, or null for an unrecognized instruction sequence.</returns>
    internal static int? Arm64TargetOffset(uint first, uint second, uint third)
    {
        // FixupPrecode loads its target into x11 and branches immediately; StubPrecode also loads a secret parameter into x12.
        var fixup = (first & 0xFF00001F) == 0x5800000B && second == 0xD61F0160;
        var stub = (first & 0xFF00001F) == 0x5800000A && (second & 0xFF00001F) == 0x5800000C && third == 0xD61F0140;
        return fixup || stub ? (unchecked((int)(first << 8)) >> 13) * 4 : null;
    }
}
