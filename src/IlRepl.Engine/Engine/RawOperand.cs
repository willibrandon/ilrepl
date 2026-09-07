namespace IlRepl.Engine;

/// <summary>
/// The operand of a decoded instruction, as bytes: an integer (sign-extended for the signed
/// forms, a token for the metadata forms), the raw bits of a float, or the targets of a switch.
/// Nothing is resolved or widened here.
/// </summary>
/// <param name="Integer">The integer operand, the unsigned token, or the local or argument index; 0 when there is none.</param>
/// <param name="Bits32">The four operand bytes of <c>ldc.r4</c> as they are; 0 otherwise.</param>
/// <param name="Bits64">The eight operand bytes of <c>ldc.r8</c> as they are; 0 otherwise.</param>
/// <param name="SwitchTargets">The absolute targets of a <c>switch</c>, in table order; empty otherwise.</param>
public readonly record struct RawOperand(long Integer, uint Bits32, ulong Bits64, int[] SwitchTargets)
{
    /// <summary>
    /// An operand for an instruction that has none.
    /// </summary>
    public static RawOperand None { get; } = new(0, 0, 0, []);

    /// <summary>
    /// The operand as a metadata token.
    /// </summary>
    public int Token => unchecked((int)Integer);
}
