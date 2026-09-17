namespace IlRepl.Engine;

/// <summary>
/// Tracks an exactly reconstructed register value and the instruction operands that formed it.
/// </summary>
/// <param name="Value">The reconstructed unsigned register bits.</param>
/// <param name="Parts">The responsible operand components.</param>
/// <param name="Page">Whether an ADRP produced an incomplete page address.</param>
internal sealed record NativeRegisterValue(ulong Value, List<NativeAddressPart> Parts, bool Page = false);
