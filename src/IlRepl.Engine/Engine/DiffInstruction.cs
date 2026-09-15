namespace IlRepl.Engine;

/// <summary>
/// An instruction's comparison identity, displayed text, and independently computed stack state.
/// </summary>
/// <param name="Key">The normalized identity used by the sequence comparison.</param>
/// <param name="Text">The original displayed instruction.</param>
/// <param name="Stack">The computed stack state after the instruction.</param>
internal sealed record DiffInstruction(string Key, string Text, string? Stack)
{
    /// <summary>
    /// The instruction indices targeted by this branch or switch.
    /// </summary>
    internal IReadOnlyList<int> Targets { get; init; } = [];
}
