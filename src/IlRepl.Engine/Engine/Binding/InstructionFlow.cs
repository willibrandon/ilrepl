using System.Reflection.Emit;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Identifies instructions that make an implied return unnecessary at the end of a body.
/// </summary>
internal static class InstructionFlow
{
    /// <summary>
    /// Tests whether execution can fall through to the instruction after this opcode.
    /// </summary>
    /// <param name="op">The opcode.</param>
    /// <returns>Whether the current control-flow path ends.</returns>
    public static bool EndsPath(OpCode op) => op.FlowControl is FlowControl.Return or FlowControl.Throw
        || op == OpCodes.Br || op == OpCodes.Br_S || op == OpCodes.Jmp;
}
