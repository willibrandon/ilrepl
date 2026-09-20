using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// Verifies entry stub decoding proves target cells from instructions without assuming the operating system's page size.
/// </summary>
[TestClass]
public sealed class NativeEntryPointTests
{
    /// <summary>
    /// CoreCLR fixup and regular entry stubs yield the signed PC-relative target displacement encoded by the load.
    /// </summary>
    /// <param name="first">The literal load instruction.</param>
    /// <param name="second">The branch or secret-parameter load instruction.</param>
    /// <param name="third">The regular stub's branch, when needed.</param>
    /// <param name="expected">The encoded target cell displacement in bytes.</param>
    [TestMethod]
    [DataRow(0x5802000BU, 0xD61F0160U, 0U, 16384)]
    [DataRow(0x5804000BU, 0xD61F0160U, 0U, 32768)]
    [DataRow(0x5808000BU, 0xD61F0160U, 0U, 65536)]
    [DataRow(0x58FE000BU, 0xD61F0160U, 0U, -16384)]
    [DataRow(0x5802004AU, 0x5801FFECU, 0xD61F0140U, 16392)]
    public void Arm64TargetOffset_RecognizesRuntimeStubs(uint first, uint second, uint third, int expected)
    {
        Assert.AreEqual(expected, NativeEntryPoint.Arm64TargetOffset(first, second, third));
    }

    /// <summary>
    /// Other loads, mismatched branch registers, and overwritten targets provide no entry cell evidence.
    /// </summary>
    /// <param name="first">The candidate literal load instruction.</param>
    /// <param name="second">The following instruction.</param>
    /// <param name="third">The candidate regular stub's branch instruction.</param>
    [TestMethod]
    [DataRow(0x1802000BU, 0xD61F0160U, 0U)]
    [DataRow(0x5802000BU, 0xD61F0140U, 0U)]
    [DataRow(0x5802000CU, 0xD61F0180U, 0U)]
    [DataRow(0x5802004AU, 0x5801FFEAU, 0xD61F0140U)]
    [DataRow(0x5802004AU, 0x5801FFECU, 0xD61F0160U)]
    [DataRow(0xD65F03C0U, 0U, 0U)]
    public void Arm64TargetOffset_RejectsUnrecognizedInstructions(uint first, uint second, uint third)
    {
        Assert.IsNull(NativeEntryPoint.Arm64TargetOffset(first, second, third));
    }

    /// <summary>
    /// CoreCLR x64 fixup and regular entry stubs yield the distance from the entry to the cell their jump goes through.
    /// </summary>
    /// <param name="stub">The bytes at the entry point.</param>
    /// <param name="expected">The target cell displacement in bytes.</param>
    [TestMethod]
    [DataRow("FF25FA3F00004C8B15FB3F0000FF25FD3F000090", 0x4000)]
    [DataRow("FF25FABFFFFF4C8B15FBBFFFFFFF25FDBFFFFF90", -0x4000)]
    [DataRow("4C8B15F93F0000FF25F33F0000CCCCCCCCCCCCCC", 0x4000)]
    public void X64TargetOffset_RecognizesRuntimeStubs(string stub, int expected)
    {
        Assert.AreEqual(expected, NativeEntryPoint.X64TargetOffset(Convert.FromHexString(stub)));
    }

    /// <summary>
    /// Compiled code that starts like a stub, a tail jump, and a short read provide no entry cell evidence.
    /// </summary>
    /// <param name="code">The bytes at the entry point.</param>
    [TestMethod]
    [DataRow("FF25FA3F0000488B0148FFC0C3CCCCCCCCCCCCCC")]
    [DataRow("FF25FA3F00004C8B15FB3F0000FFE0CCCCCCCCCC")]
    [DataRow("4C8B01498BC0C3CCCCCCCCCCCCCCCCCCCCCCCCCC")]
    [DataRow("4C8B15F93F0000FFE0CCCCCCCCCCCCCCCCCCCCCC")]
    [DataRow("48FF25FA3F0000CCCCCCCCCCCCCCCCCCCCCCCCCC")]
    [DataRow("B82A000000C3CCCCCCCCCCCCCCCCCCCCCCCCCCCC")]
    [DataRow("FF25FA3F00004C8B15FB3F0000FF")]
    public void X64TargetOffset_RejectsUnrecognizedInstructions(string code)
    {
        Assert.IsNull(NativeEntryPoint.X64TargetOffset(Convert.FromHexString(code)));
    }
}
