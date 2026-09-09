using System.Reflection;

namespace IlRepl.Engine.Binding;

/// <summary>
/// Copies full assembly identity for metadata-only matching against already loaded assemblies.
/// </summary>
internal sealed record LoadedAssemblyIdentity(
    string Name, Version? Version, string Culture, string PublicKeyToken, AssemblyContentType Content)
{
    /// <summary>
    /// Copies identity components without keeping the mutable AssemblyName supplied by the caller.
    /// </summary>
    /// <param name="name">The definition or reference identity.</param>
    /// <returns>The copied identity.</returns>
    public static LoadedAssemblyIdentity Of(AssemblyName name) => new(name.Name ?? "", name.Version,
        name.CultureName ?? "", Convert.ToHexString(name.GetPublicKeyToken() ?? []), name.ContentType);

    /// <summary>
    /// Applies loaded-assembly version unification without substituting another culture or signing identity.
    /// </summary>
    /// <param name="requested">The assembly reference.</param>
    /// <returns>Whether this loaded definition can satisfy the reference.</returns>
    public bool Satisfies(LoadedAssemblyIdentity requested) =>
        string.Equals(Name, requested.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Culture, requested.Culture, StringComparison.OrdinalIgnoreCase)
        && PublicKeyToken == requested.PublicKeyToken && Content == requested.Content
        && (requested.Version is null || Version is not null && Version >= requested.Version);
}
