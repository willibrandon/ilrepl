namespace IlRepl.Engine;

/// <summary>
/// Identifies a stack slot and the requirement checked by the instruction validator.
/// </summary>
/// <param name="Index">The bottom-based slot, or -1 for a missing value.</param>
/// <param name="Role">The operand's role.</param>
/// <param name="Expected">The required type or category.</param>
internal sealed record StackRequirement(int Index, string Role, string Expected);
