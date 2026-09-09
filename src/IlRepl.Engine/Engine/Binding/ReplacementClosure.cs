namespace IlRepl.Engine.Binding;

/// <summary>
/// Contains every family and method transitively affected by a replaced definition.
/// </summary>
/// <remarks>
/// The definitions that must be rebuilt with a replaced one: every family and method that
/// mentions it, or mentions something that does, to any depth.
/// </remarks>
/// <typeparam name="TFamily">A type family record.</typeparam>
/// <typeparam name="TMethod">A session method record.</typeparam>
/// <param name="Families">The dependent families, in the order they were found.</param>
/// <param name="Methods">The dependent methods, in the order they were found.</param>
public sealed record ReplacementClosure<TFamily, TMethod>(IReadOnlyList<TFamily> Families, IReadOnlyList<TMethod> Methods)
{
    /// <summary>
    /// True when nothing depends on the replaced definition.
    /// </summary>
    public bool IsEmpty => Families.Count == 0 && Methods.Count == 0;
}
