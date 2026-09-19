using System.Reflection.Metadata;

namespace IlRepl.Engine;

/// <summary>
/// Translates assembly and type arguments for activation without modifying the caller's strings.
/// </summary>
internal static partial class CopiedTypeNames
{
    /// <summary>
    /// Finds copied types within valid assembly-scoped activation names, including constructed generic arguments.
    /// </summary>
    /// <param name="assembly">The supplied assembly identity, or null for the original caller's assembly.</param>
    /// <param name="name">The supplied type name.</param>
    /// <param name="ignoreCase">Whether activation ignores the type name's case.</param>
    /// <param name="context">The original caller's assembly identity.</param>
    /// <param name="names">Triples containing original and copied type identities.</param>
    /// <returns>The translated qualified type name, or null when the original call should be retained.</returns>
    internal static string? TranslateActivation(string? assembly, string? name, bool ignoreCase, string context, string[] names)
    {
        if (!AssemblyNameInfo.TryParse((assembly ?? context).AsSpan(), out var identity)
            || name is null || !TypeName.TryParse(name.AsSpan(), out var type) || type.AssemblyName is not null)
        {
            return null;
        }

        var qualified = TypeName.Parse((type.FullName + ", " + identity.FullName).AsSpan());
        var translated = TranslateType(qualified, ignoreCase, context, names, false);
        if (translated.AssemblyQualifiedName == qualified.AssemblyQualifiedName)
        {
            return null;
        }

        return translated.AssemblyQualifiedName;
    }
}
