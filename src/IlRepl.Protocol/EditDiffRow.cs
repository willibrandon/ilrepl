namespace IlRepl.Protocol;

/// <summary>
/// An instruction or metadata difference with the independent stack state from each side.
/// </summary>
/// <param name="Kind">Equal, removed, added, or metadata.</param>
/// <param name="Original">The original instruction or metadata text.</param>
/// <param name="Edited">The edited instruction or metadata text.</param>
/// <param name="OriginalStack">The original post-instruction stack, including unknown and unreachable states.</param>
/// <param name="EditedStack">The edited post-instruction stack.</param>
public sealed record EditDiffRow(string Kind, string? Original, string? Edited, string? OriginalStack, string? EditedStack);
