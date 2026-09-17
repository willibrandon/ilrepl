using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies intrinsic capability presentation groups observed extensions without repeating nested API variants.
/// </summary>
[TestClass]
public sealed class NativeInstructionSetsTests
{
    /// <summary>
    /// Observed x86 and Arm sets have architectural names while vector helpers, base classes, and nested duplicates are omitted.
    /// </summary>
    [TestMethod]
    public void Format_GroupsAndSortsObservedSetsWithoutNestedDuplicates()
    {
        string[] supported =
        [
            "System.Runtime.Intrinsics.X86.Popcnt+X64", "System.Runtime.Intrinsics.X86.Avx512Vbmi",
            "System.Runtime.Intrinsics.X86.Avx512DQ", "System.Runtime.Intrinsics.X86.Avx2",
            "System.Runtime.Intrinsics.X86.Avx512F", "System.Runtime.Intrinsics.X86.Avx512BW",
            "System.Runtime.Intrinsics.X86.Avx512CD", "System.Runtime.Intrinsics.X86.Avx512F+X64",
            "System.Runtime.Intrinsics.X86.Avx512BW+VL", "System.Runtime.Intrinsics.X86.Avx2+X64",
            "System.Runtime.Intrinsics.X86.Bmi2", "System.Runtime.Intrinsics.X86.Fma",
            "System.Runtime.Intrinsics.X86.Lzcnt", "System.Runtime.Intrinsics.X86.Popcnt",
            "System.Runtime.Intrinsics.X86.Sse41", "System.Runtime.Intrinsics.X86.Sse42",
            "System.Runtime.Intrinsics.X86.X86Base", "System.Runtime.Intrinsics.Vector128",
            "System.Runtime.Intrinsics.Vector256", "System.Runtime.Intrinsics.Vector512",
        ];
        const string expected = "AVX-512F/BW/CD/DQ/VBMI, AVX2, BMI2, FMA, LZCNT, POPCNT, SSE4.1, SSE4.2";

        Assert.AreEqual(expected, NativeInstructionSets.Format(supported));
        Assert.AreEqual(expected, NativeInstructionSets.Format(supported.Reverse().ToArray()));

        string[] arm =
        [
            "System.Runtime.Intrinsics.Arm.ArmBase", "System.Runtime.Intrinsics.Arm.ArmBase+Arm64",
            "System.Runtime.Intrinsics.Arm.AdvSimd", "System.Runtime.Intrinsics.Arm.AdvSimd+Arm64",
            "System.Runtime.Intrinsics.Arm.Aes", "System.Runtime.Intrinsics.Arm.Crc32+Arm64",
            "System.Runtime.Intrinsics.Arm.Sha256", "System.Runtime.Intrinsics.Vector128",
        ];
        Assert.AreEqual("AES, AdvSIMD, CRC32, SHA256", NativeInstructionSets.Format(arm));
        Assert.AreEqual("AES, AdvSIMD, CRC32, SHA256", NativeInstructionSets.Format(arm.Reverse().ToArray()));
    }
}
