using IlRepl.Engine.Binding;

namespace IlRepl.Engine;

/// <summary>
/// A local variable declared with <c>.locals</c>.
/// </summary>
/// <param name="Type">The local's type.</param>
/// <param name="Name">The local's name, or null when it was declared by index only.</param>
/// <param name="IsPinned">True when the local was declared <c>pinned</c>.</param>
public sealed record LocalDeclaration(Type Type, string? Name, bool IsPinned)
{
    /// <summary>
    /// The exact type retained when its runtime projection cannot represent its complete shape.
    /// </summary>
    internal TypeSymbol? ExactType { get; init; }
}
