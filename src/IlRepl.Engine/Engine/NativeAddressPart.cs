namespace IlRepl.Engine;

/// <summary>
/// Identifies an immediate component whose meaning is proven by a later native pointer use.
/// </summary>
/// <param name="Line">The instruction line index.</param>
/// <param name="Start">The component's character offset.</param>
/// <param name="Length">The original component length.</param>
/// <param name="Shift">The wide-move bit offset, or minus one for a full immediate.</param>
/// <param name="Operation">The transformation applied to the symbolic address.</param>
/// <param name="Adjustment">The arithmetic added after this immediate was formed.</param>
internal sealed record NativeAddressPart(int Line, int Start, int Length, int Shift, string Operation = "", ulong Adjustment = 0);
