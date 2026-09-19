using IlRepl.Engine;

namespace IlRepl.Tests.Engine;

/// <summary>
/// An Arm64 address load compares equal however many instructions the address took to build.
/// </summary>
[TestClass]
public sealed class NativeAddressLoadsTests
{
    private const string Field = "<static-field:Owner, ilrepl.types.1559, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
        + "::Value:System.Object>";

    /// <summary>
    /// The JIT omits the move for a zero part, so two processes can load one field in three instructions and in two.
    /// </summary>
    [TestMethod]
    public void Fold_LoadsOfOneAddressAreEqualWhateverTheirLength()
    {
        string[] left =
        [
            "    mov     fp, sp",
            "L02:",
            "    movz    x0, bits0:15(" + Field + ")",
            "    movk    x0, bits16:31(" + Field + ") LSL #16",
            "    movk    x0, bits32:47(" + Field + ") LSL #32",
            "    ldr     x0, [x0]",
        ];

        string[] right =
        [
            "    mov     fp, sp",
            "L02:",
            "    movz    x0, bits0:15(" + Field + ")",
            "    movk    x0, bits32:47(" + Field + ") LSL #32",
            "    ldr     x0, [x0]",
        ];

        var folded = NativeAddressLoads.Fold(left);

        Assert.AreSequenceEqual(folded, NativeAddressLoads.Fold(right));
        Assert.AreSequenceEqual(["    mov     fp, sp", "L02:", "    mov     x0, " + Field, "    ldr     x0, [x0]"], folded);
        Assert.IsEmpty(NativeDifference.Create(folded, NativeAddressLoads.Fold(right), "Read", "Read"));
    }

    /// <summary>
    /// Loads of different addresses, or into different registers, stay apart, and other instructions pass through.
    /// </summary>
    [TestMethod]
    public void Fold_KeepsDifferentLoadsApart()
    {
        string[] lines =
        [
            "    movz    x0, bits0:15(<type:A>)",
            "    movk    x0, bits16:31(<type:A>) LSL #16",
            "    movz    x1, bits0:15(<type:A>)",
            "    movz    x1, bits0:15(<type:B>)",
            "    movz    x2, #42",
        ];

        Assert.AreSequenceEqual(
            ["    mov     x0, <type:A>", "    mov     x1, <type:A>", "    mov     x1, <type:B>", "    movz    x2, #42"],
            NativeAddressLoads.Fold(lines));
    }
}
