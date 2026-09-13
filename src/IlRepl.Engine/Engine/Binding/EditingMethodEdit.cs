namespace IlRepl.Engine.Binding;

/// <summary>
/// Captures an edit's original metadata identity for source analysis without executing or importing a new assembly.
/// </summary>
/// <param name="Name">The edit name.</param>
/// <param name="Reference">The source reference supplied when the original was captured.</param>
/// <param name="Method">The original method definition, including its declaring context.</param>
/// <param name="Revision">The committed revision represented by the snapshot.</param>
internal sealed record EditingMethodEdit(string Name, string Reference, MethodSymbol Method, int Revision)
{
    /// <summary>
    /// The original operand type identities retained even when their live session names are replaced.
    /// </summary>
    internal IReadOnlyDictionary<string, TypeSymbol> ContextTypes { get; init; } = new Dictionary<string, TypeSymbol>();
}
