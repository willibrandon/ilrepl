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
}
