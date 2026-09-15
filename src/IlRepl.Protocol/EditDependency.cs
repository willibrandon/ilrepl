namespace IlRepl.Protocol;

/// <summary>
/// A dependency and the decision made while reproducing its declaring context.
/// </summary>
/// <param name="Symbol">The exact referenced symbol.</param>
/// <param name="Assembly">The assembly identity of the original symbol.</param>
/// <param name="Location">The source method and instruction.</param>
/// <param name="Disposition">Whether the symbol is copied or remains an external reference.</param>
public sealed record EditDependency(string Symbol, string Assembly, string Location, string Disposition)
{
    /// <summary>
    /// The source member's required access, retained alongside the import decision.
    /// </summary>
    public string Access { get; init; } = "";
}
