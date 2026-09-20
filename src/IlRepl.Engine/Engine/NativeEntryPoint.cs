using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace IlRepl.Engine;

/// <summary>
/// Recognizes runtime entry stubs whose instructions prove the location of a method's target cell.
/// </summary>
internal static class NativeEntryPoint
{
    /// <summary>
    /// The number of entry bytes that tell an x64 entry stub from compiled code.
    /// </summary>
    internal const int X64StubLength = 15;

    /// <summary>
    /// Reads a known live method entry point and returns its proven indirection cell, or zero for an unrecognized stub.
    /// </summary>
    /// <param name="entry">The entry point obtained from the method's runtime handle.</param>
    /// <returns>The target cell address when the entry instructions establish it.</returns>
    internal static nint IndirectionCell(nint entry)
    {
        if (entry == 0)
        {
            return 0;
        }

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => Arm64Cell(entry),
            Architecture.X64 => X64Cell(entry),
            _ => 0,
        };
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

    /// <summary>
    /// Decodes the PC-relative target jump only when the surrounding instructions match a CoreCLR x64 entry stub.
    /// </summary>
    /// <remarks>
    /// On x64 a listing shows this cell's address only when the JIT cannot reach the cell with a 32-bit displacement. It then
    /// loads the address into a register and calls through it. Where the runtime placed the code and the cell decides that, so
    /// the same method can take either form from one process to the next.
    /// </remarks>
    /// <param name="stub">The first <see cref="X64StubLength"/> bytes at the entry point.</param>
    /// <returns>The signed byte displacement of the target cell from the entry, or null for an unrecognized sequence.</returns>
    internal static int? X64TargetOffset(ReadOnlySpan<byte> stub)
    {
        if (stub.Length < X64StubLength)
        {
            return null;
        }

        // FixupPrecode jumps through its target, then loads the method into r10 and jumps to the fixup thunk.
        if (IsJump(stub) && IsLoad(stub[6..]) && IsJump(stub[13..]))
        {
            return 6 + BinaryPrimitives.ReadInt32LittleEndian(stub[2..]);
        }

        // StubPrecode loads its secret parameter into r10 and then jumps through its target.
        if (IsLoad(stub) && IsJump(stub[7..]))
        {
            return 13 + BinaryPrimitives.ReadInt32LittleEndian(stub[9..]);
        }

        return null;

        static bool IsJump(ReadOnlySpan<byte> code) => code[0] == 0xFF && code[1] == 0x25;

        static bool IsLoad(ReadOnlySpan<byte> code) => code[0] == 0x4C && code[1] == 0x8B && code[2] == 0x15;
    }

    private static nint Arm64Cell(nint entry)
    {
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

    private static nint X64Cell(nint entry)
    {
        // A method that is already compiled has its code here instead of a stub. Its first bytes nearly always differ from a
        // stub's, and then nothing past them is read.
        var first = unchecked((ushort)Marshal.ReadInt16(entry));
        if (first is not (0x25FF or 0x8B4C))
        {
            return 0;
        }

        Span<byte> stub = stackalloc byte[X64StubLength];
        for (var index = 0; index < stub.Length; index++)
        {
            stub[index] = Marshal.ReadByte(entry, index);
        }

        var offset = X64TargetOffset(stub);
        return offset.HasValue ? entry + offset.Value : 0;
    }
}
